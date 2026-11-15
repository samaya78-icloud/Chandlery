using System.Security.Cryptography;
using System.Text;
using Chandlery.Models;
using Chandlery.Services;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Chandlery.Sync.Tests;

/// <summary>What may be attached, how photos are shrunk, and that a file follows its message: it reaches the other side intact, and never reaches anyone who may not read the message.</summary>
public class ChatFileRulesTests
{
    internal static byte[] Pdf(int extra = 0) => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\n" + new string('x', extra) + "\n%%EOF");

    /// <summary>A picture with a pattern that does not compress to nothing, in the given format.</summary>
    internal static byte[] Picture(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 90, int noise = 6)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(7);
        for (var y = 0; y < height; y += 1)
            for (var x = 0; x < width; x += 1)
                bitmap.SetPixel(x, y, new SKColor((byte)((x / 4 + random.Next(noise)) % 256), (byte)((y / 4 + random.Next(noise)) % 256), (byte)(((x + y) / 8 + random.Next(noise)) % 256)));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, quality).ToArray();
    }

    [Fact]
    public void TheKindOfAFileIsReadFromItsBytes_NotFromItsNameOrWhatTheBrowserSays()
    {
        Assert.Equal("application/pdf", FileRules.Sniff(Pdf()));
        Assert.Equal("image/png", FileRules.Sniff(Picture(8, 8)));
        Assert.Equal("image/jpeg", FileRules.Sniff(Picture(8, 8, SKEncodedImageFormat.Jpeg)));
        Assert.Null(FileRules.Sniff(Encoding.ASCII.GetBytes("MZ this is a program")));
        Assert.Null(FileRules.Sniff([]));

        Assert.Equal("application/pdf", FileRules.Check("statement.pdf", Pdf()));
        Assert.Contains("not the kind of file", Assert.Throws<InvalidOperationException>(() => FileRules.Check("photo.jpg", Pdf())).Message);                   // a PDF dressed as a photo
        Assert.Contains("not the kind of file", Assert.Throws<InvalidOperationException>(() => FileRules.Check("photo.png", Encoding.ASCII.GetBytes("MZ program"))).Message);
        Assert.Contains("only images", Assert.Throws<InvalidOperationException>(() => FileRules.Check("run.exe", Pdf())).Message);
        Assert.Contains("is empty", Assert.Throws<InvalidOperationException>(() => FileRules.Check("a.pdf", [])).Message);
        Assert.Contains("larger than", Assert.Throws<InvalidOperationException>(() => FileRules.Check("big.pdf", Pdf(FileRules.MaxPickedBytes))).Message);
    }

    [Fact]
    public void ABigPhotoIsShrunkToAJpegThatCanBeSent_ASmallOneIsLeftAlone_AndAPdfIsNeverTouched()
    {
        var big = Picture(3200, 2400);
        Assert.True(big.Length > FileRules.MaxStoredBytes);                                                // too big to keep as it is
        var (data, name, type) = ImageShrinker.Shrink("crane damage.png", "image/png", big);
        Assert.Equal(("image/jpeg", "crane damage.jpg"), (type, name));
        Assert.InRange(data.Length, 1, FileRules.MaxStoredBytes);
        using var shrunk = SKBitmap.Decode(data);
        Assert.Equal(FileRules.LongestSidePixels, Math.Max(shrunk.Width, shrunk.Height));
        Assert.Equal(3200d / 2400d, (double)shrunk.Width / shrunk.Height, 2);                               // the shape is kept

        var small = Picture(400, 300, SKEncodedImageFormat.Jpeg, 70);
        Assert.Same(small, ImageShrinker.Shrink("small.jpg", "image/jpeg", small).Data);

        var pdf = Pdf();
        Assert.Same(pdf, ImageShrinker.Shrink("s.pdf", "application/pdf", pdf).Data);

        var broken = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };                // looks like a PNG, is not one
        Assert.Contains("cannot be read", Assert.Throws<InvalidOperationException>(() => ImageShrinker.Shrink("x.png", "image/png", broken)).Message);
    }
}

public class ChatFileTests : IAsyncLifetime
{
    private readonly TwoSites _s = new();
    private ChatService _shore = null!, _vessel = null!;
    private ProcurementService _procurement = null!;
    private CrewMember _chief = null!, _cook = null!;
    private readonly Guid _po = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        _shore = new ChatService(_s.ShoreFactory);
        _vessel = new ChatService(_s.VesselFactory);
        _procurement = new ProcurementService(_s.ShoreFactory);
        await using (var db = await _s.ShoreFactory.CreateDbContextAsync())
        {
            foreach (var module in new[] { PolicyModules.Conversations, PolicyModules.ConversationsWithSuppliers })
                db.VesselModuleSwitches.Add(new VesselModuleSwitch { CompanyId = _s.CompanyId, VesselId = _s.VesselId, Module = module, Enabled = true });
            await db.SaveChangesAsync();
        }
        _chief = await SignOnAsync("FL1", "Chief Officer");
        _cook = await SignOnAsync("FL2", "Cook");
    }

    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<CrewMember> SignOnAsync(string code, string rank)
    {
        var crew = await _s.ShoreCrew.SaveAsync(_s.CompanyId, new CrewInput(null, code, $"Name of {code}", rank, "Singaporean", null, "Passport", "E1234567X", null, "", "", _s.VesselId, null, true, false), _s.User);
        await _s.SettleUntilQuietAsync();
        return crew;
    }

    private static Actor As(CrewMember crew) => new(crew.UserId, crew.FullName);

    private static PendingAttachment File(string name, byte[] data) => new() { FileName = name, Data = data };

    private ChatPost Post(string body, ChatChannel channel = ChatChannel.Internal, params PendingAttachment[] files)
        => new(ChatModules.PurchaseOrder, _po, _s.VesselId, channel, body, Files: files);

    private async Task<int> FilesOnAsync(Microsoft.EntityFrameworkCore.IDbContextFactory<Chandlery.Data.ChandleryDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.FileAttachments.IgnoreQueryFilters().CountAsync();
    }

    [Fact]
    public async Task APhotoTakenOnboardReachesTheOfficeIntact_ShrunkOnTheWay_AndOpensOnlyForThoseWhoReadTheMessage()
    {
        var photo = ChatFileRulesTests.Picture(2400, 1600);
        var message = await _vessel.PostAsync(_s.CompanyId, As(_chief), Post("Dented drum, see photo", ChatChannel.Internal, File("drum.png", photo)));
        await _s.SettleUntilQuietAsync();

        var onboard = (await _vessel.ThreadAsync(_s.CompanyId, As(_chief), ChatModules.PurchaseOrder, _po)).Items.Single();
        var ashore = (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po)).Items.Single();
        var seen = Assert.Single(ashore.Files!);
        Assert.Equal(("drum.jpg", "image/jpeg", true), (seen.FileName, seen.ContentType, seen.IsImage));
        Assert.Equal(onboard.Files!.Single().Id, seen.Id);

        var here = await _vessel.OpenFileAsync(As(_chief), seen.Id);
        var there = await _shore.OpenFileAsync(_s.User, seen.Id);
        Assert.NotNull(here);
        Assert.NotNull(there);
        Assert.Equal(here!.Content, there!.Content);                                                                  // the same bytes, both sides
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(there.Content)), there.Sha256);
        Assert.True(there.Content.Length <= FileRules.MaxStoredBytes);
        Assert.Equal(1, await FilesOnAsync(_s.ShoreFactory));
        Assert.Equal(message.Id, there.OwnerId);
    }

    [Fact]
    public async Task AFileOnAnOfficeOnlyMessageNeverReachesTheVessel_AndAFileCanBeSentWithNoWords()
    {
        await _shore.PostAsync(_s.CompanyId, _s.User, Post("Confidential quote attached", ChatChannel.OfficeOnly, File("quote.pdf", ChatFileRulesTests.Pdf())));
        var shared = await _shore.PostAsync(_s.CompanyId, _s.User, Post("", ChatChannel.Internal, File("note.pdf", ChatFileRulesTests.Pdf(10))));      // only a file
        await _s.SettleUntilQuietAsync();

        Assert.Equal(2, await FilesOnAsync(_s.ShoreFactory));
        Assert.Equal(1, await FilesOnAsync(_s.VesselFactory));                                                        // the office-only one stayed ashore, even after a full baseline
        var vesselSees = (await _vessel.ThreadAsync(_s.CompanyId, As(_chief), ChatModules.PurchaseOrder, _po)).Items.Single();
        Assert.Equal(shared.Id, vesselSees.Message.Id);
        Assert.Equal("", vesselSees.Body);
        Assert.Equal("note.pdf", vesselSees.Files!.Single().FileName);

        var officeOnly = (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po)).Items.First(i => i.Message.Channel == ChatChannel.OfficeOnly).Files!.Single();
        Assert.Null(await _vessel.OpenFileAsync(As(_chief), officeOnly.Id));                                          // not even by its address
        Assert.NotNull(await _shore.OpenFileAsync(_s.User, officeOnly.Id));
    }

    [Fact]
    public async Task ARefusedFileLeavesNothingBehind_AndARemovedMessageGivesUpItsFile()
    {
        var tooMany = new[] { File("a.pdf", ChatFileRulesTests.Pdf(1)), File("b.pdf", ChatFileRulesTests.Pdf(2)), File("c.pdf", ChatFileRulesTests.Pdf(3)), File("d.pdf", ChatFileRulesTests.Pdf(4)) };
        Assert.Contains("up to 3", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PostAsync(_s.CompanyId, _s.User, Post("four", ChatChannel.Internal, tooMany)))).Message);
        Assert.Contains("larger than", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PostAsync(_s.CompanyId, _s.User, Post("big pdf", ChatChannel.Internal, File("big.pdf", ChatFileRulesTests.Pdf(FileRules.MaxStoredBytes)))))).Message);
        Assert.Contains("not the kind", (await Assert.ThrowsAsync<InvalidOperationException>(() => _shore.PostAsync(_s.CompanyId, _s.User, Post("fake", ChatChannel.Internal, File("fake.png", ChatFileRulesTests.Pdf()))))).Message);
        Assert.Empty((await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po)).Items);                 // no message, no file
        Assert.Equal(0, await FilesOnAsync(_s.ShoreFactory));

        var posted = await _shore.PostAsync(_s.CompanyId, _s.User, Post("with a file", ChatChannel.Internal, File("keep.pdf", ChatFileRulesTests.Pdf())));
        var file = (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po)).Items.Single().Files!.Single();
        Assert.NotNull(await _shore.OpenFileAsync(_s.User, file.Id));
        await _shore.DeleteAsync(_s.CompanyId, _s.User, posted.Id);
        Assert.Null(await _shore.OpenFileAsync(_s.User, file.Id));                                                     // removed with its message
        Assert.Null((await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.PurchaseOrder, _po)).Items.Single().Files);
    }

    [Fact]
    public async Task ASupplierSendsAndOpensFilesInItsOwnThreadOnly()
    {
        await using var db = await _s.ShoreFactory.CreateDbContextAsync();
        var linked = await db.CompanySuppliers.Where(l => l.CompanyId == _s.CompanyId).Select(l => l.SupplierId).OrderBy(i => i).ToListAsync();
        var (a, b) = (linked[0], linked[1]);
        async Task<Actor> UserFor(Guid supplier, string name)
        {
            await using var d = await _s.ShoreFactory.CreateDbContextAsync();
            var user = new AppUser { Id = Guid.CreateVersion7(), Email = $"{name}{Guid.NewGuid():N}@supplier.example.com", PasswordHash = "x", Role = UserRole.SupplierUser, DisplayName = name, SupplierId = supplier, CreatedAt = DateTime.UtcNow };
            d.Users.Add(user);
            await d.SaveChangesAsync();
            return new Actor(user.Id, name);
        }
        var (anna, bram) = (await UserFor(a, "Anna"), await UserFor(b, "Bram"));
        var requisition = await _s.ShoreRequisitions.CreateAsync(_s.CompanyId, _s.NewRequisition("spares"), _s.User);
        var rfq = await _procurement.CreateRfqAsync(_s.CompanyId, new NewRfq
        {
            RequisitionId = requisition.Id, BuyerName = "buyer", InvitedSupplierIds = [a, b],
            Lines = requisition.Lines.Select(l => new NewRfqLine { ItemId = l.ItemId, Quantity = l.Quantity, RequisitionLineId = l.Id }).ToList(),
        }, _s.User);
        ChatPost OnRfq(string body, ChatChannel channel, string topic, params PendingAttachment[] files) => new(ChatModules.Rfq, rfq.Id, _s.VesselId, channel, body, topic, Files: files);

        await _shore.PostAsync(_s.CompanyId, _s.User, OnRfq("Our spec sheet", ChatChannel.Shared, ChatRules.SupplierTopic(a), File("spec.pdf", ChatFileRulesTests.Pdf(5))));
        await _shore.PostAsync(_s.CompanyId, _s.User, OnRfq("For B only", ChatChannel.Shared, ChatRules.SupplierTopic(b), File("b-only.pdf", ChatFileRulesTests.Pdf(6))));
        await _shore.PostAsync(_s.CompanyId, _s.User, OnRfq("Internal comparison", ChatChannel.Internal, "", File("internal.pdf", ChatFileRulesTests.Pdf(7))));
        await _shore.PostAsync(_s.CompanyId, anna, OnRfq("Our quote sheet", ChatChannel.Shared, "", File("quote.pdf", ChatFileRulesTests.Pdf(8))));

        var annaSees = (await _shore.ThreadAsync(_s.CompanyId, anna, ChatModules.Rfq, rfq.Id)).Items;
        Assert.Equal(["spec.pdf", "quote.pdf"], annaSees.Select(i => i.Files!.Single().FileName));
        Assert.All(annaSees.Select(i => i.Files!.Single().Id), id => Assert.NotNull(_shore.OpenFileAsync(anna, id).Result));

        var officeSees = (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Rfq, rfq.Id, ChatRules.SupplierTopic(b))).Items.Single().Files!.Single();
        Assert.Null(await _shore.OpenFileAsync(anna, officeSees.Id));                                                 // another supplier's file, by its address
        Assert.NotNull(await _shore.OpenFileAsync(bram, officeSees.Id));
        var internalFile = (await _shore.ThreadAsync(_s.CompanyId, _s.User, ChatModules.Rfq, rfq.Id)).Items.Single().Files!.Single();
        Assert.Null(await _shore.OpenFileAsync(anna, internalFile.Id));                                               // the company's own file
        Assert.Null(await _shore.OpenFileAsync(bram, internalFile.Id));
    }
}
