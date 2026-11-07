using System.Security.Claims;
using Chandlery.Components;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
using Chandlery.Services.Sync;
using Marine.Sync;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<ChandleryDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Chandlery")));

builder.Services.AddChandlerySync(builder.Configuration, NodeContext.Current);

builder.Services.AddScoped<ProcurementService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<UomService>();
builder.Services.AddScoped<VesselGroupService>();
builder.Services.AddScoped<RequisitionService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<HubService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddScoped<PoDocumentService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<OrdersService>();
builder.Services.AddScoped<DispatchService>();
builder.Services.AddScoped<OnboardRfqService>();
builder.Services.AddSingleton(sp => CrewCrypto.From(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
builder.Services.AddScoped<CrewService>();
builder.Services.AddSingleton<IFileStore, DiskFileStore>();
builder.Services.AddHttpClient<FxFeed>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Chandlery/1.0 (exchange rate sync)");
});
builder.Services.AddScoped<FxService>();
builder.Services.AddHostedService<FxRefreshWorker>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization(o => o.AddPolicy("CrewAdmin", p => p.RequireClaim("CrewAdmin", "true")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";

        // Sessions signed in before ids became GUIDs carry numeric ids; refuse them so the person simply signs in again.
        options.Events.OnValidatePrincipal = async context =>
        {
            var user = context.Principal;
            var ids = new[] { user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, user?.FindFirst("CompanyId")?.Value, user?.FindFirst("SupplierId")?.Value };
            // A crew login that was disabled (signed off, or switched off by a crew admin) stops working at once, not at cookie expiry.
            if (user?.FindFirst("Crew") is not null && Guid.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var crewUserId))
            {
                var dbFactory = context.HttpContext.RequestServices.GetRequiredService<IDbContextFactory<ChandleryDbContext>>();
                await using var crewDb = await dbFactory.CreateDbContextAsync();
                if (!await crewDb.Users.AnyAsync(u => u.Id == crewUserId && u.IsEnabled))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }
            }
            if (ids.Any(v => v is not null && !Guid.TryParse(v, out _)))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ChandleryDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await EnsureNodeAsync(db, app.Configuration);
    // A vessel never seeds demo data of its own: it receives the company's master data from the office.
    if (!NodeContext.Current.IsVessel) await DbSeeder.SeedAsync(db);
}
else
{
    // Migrations are applied by the deployment; here we only learn which node this installation is.
    try
    {
        using var scope = app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ChandleryDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await EnsureNodeAsync(db, app.Configuration);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not read this installation's node id yet.");
    }
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// The office is the sync hub: vessels connect to /v1/sync, administrators use /console and /admin (see ChandlerySyncSetup).
if (ChandlerySyncSetup.Mode(app.Configuration) == "Hub") app.MapSyncHub();

static List<Claim> BuildClaims(AppUser user, bool crewAdmin = false)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.DisplayName),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, user.Role.ToString()),
    };
    if (user.CompanyId.HasValue)
        claims.Add(new Claim("CompanyId", user.CompanyId.Value.ToString()));
    if (user.SupplierId.HasValue)
        claims.Add(new Claim("SupplierId", user.SupplierId.Value.ToString()));
    if (user.LoginName is not null) claims.Add(new Claim("Crew", "true"));
    if (crewAdmin) claims.Add(new Claim("CrewAdmin", "true"));
    if (user.MustChangePassword) claims.Add(new Claim("MustChange", "true"));
    return claims;
}

static string DefaultHomeFor(UserRole role) => "/";

app.MapPost("/account/login", async (HttpContext http, ProcurementService procurement, CrewService crew, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Invalid request.");
    }

    var form = await http.Request.ReadFormAsync();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var user = await procurement.VerifyLoginAsync(email, password);
    if (user is null)
    {
        return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var identity = new ClaimsIdentity(BuildClaims(user, user.LoginName is not null && await crew.IsCrewAdminAsync(user.Id)), CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    if (user.MustChangePassword) return Results.Redirect("/change-password");
    var defaultHome = DefaultHomeFor(user.Role);
    var redirectTo = string.IsNullOrWhiteSpace(returnUrl) || returnUrl == "/" ? defaultHome : returnUrl;
    return Results.Redirect(redirectTo);
});

// A crew member choosing a new password (after a temporary one was handed over, or any time).
app.MapPost("/account/change-password", async (HttpContext http, CrewService crew, ProcurementService procurement, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(http); }
    catch (AntiforgeryValidationException) { return Results.BadRequest("Invalid request."); }
    if (http.User.Identity?.IsAuthenticated != true || !Guid.TryParse(http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        return Results.Redirect("/login");

    var form = await http.Request.ReadFormAsync();
    var next = form["new"].ToString();
    if (next != form["confirm"].ToString()) return Results.Redirect("/change-password?error=match");
    try
    {
        if (!await crew.ChangeOwnPasswordAsync(userId, form["current"].ToString(), next)) return Results.Redirect("/change-password?error=current");
    }
    catch (InvalidOperationException) { return Results.Redirect("/change-password?error=short"); }

    // Sign in again so the "must change" mark is gone from the session.
    var user = await procurement.FindUserByIdAsync(userId);
    if (user is null) return Results.Redirect("/login");
    var identity = new ClaimsIdentity(BuildClaims(user, await crew.IsCrewAdminAsync(user.Id)), CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).RequireAuthorization();

app.MapPost("/account/register", async (HttpContext http, ProcurementService procurement, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Invalid request.");
    }

    var form = await http.Request.ReadFormAsync();
    var companyName = form["companyName"].ToString();
    var adminName = form["adminName"].ToString();
    var email = form["email"].ToString();
    var password = form["password"].ToString();

    if (string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(adminName)
        || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
    {
        return Results.Redirect("/register?error=1");
    }

    try
    {
        var (_, admin) = await procurement.RegisterCompanyAsync(companyName, adminName, email, password);
        var identity = new ClaimsIdentity(BuildClaims(admin), CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(DefaultHomeFor(admin.Role));
    }
    catch (InvalidOperationException)
    {
        return Results.Redirect("/register?error=2");
    }
});

app.MapPost("/account/logout", async (HttpContext http, IAntiforgery antiforgery) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(http);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest("Invalid request.");
    }

    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/supplier/rfqs/export", async (HttpContext http, ProcurementService procurement, string? tab, string? q) =>
{
    var supplierClaim = http.User.FindFirst("SupplierId")?.Value;
    if (supplierClaim is null) return Results.Forbid();

    var selected = Enum.TryParse<SupplierRfqTab>(tab, true, out var parsed) ? parsed : SupplierRfqTab.Pending;
    var result = await procurement.GetSupplierRfqPageAsync(Guid.Parse(supplierClaim), selected, q, 1, int.MaxValue);

    static string Cell(string? value) => Csv.Cell(value);

    var csv = new System.Text.StringBuilder("Reference,Vessel,Type,Created,Due date,Lines,Quoted lines,Status\r\n");
    foreach (var row in result.Rows)
    {
        csv.Append(string.Join(',',
            Cell(row.Rfq.RfqNumber), Cell(row.Rfq.Vessel), Cell(row.Rfq.Type.ToString()),
            Cell(row.Rfq.CreatedDate.ToString("yyyy-MM-dd")),
            Cell(row.Rfq.DueDate?.ToString("yyyy-MM-dd")),
            Cell(row.Rfq.Lines.Count.ToString()), Cell(row.QuotedLines.ToString()), Cell(row.StatusLabel)));
        csv.Append("\r\n");
    }

    return Results.File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"rfqs-{selected.ToString().ToLower()}.csv");
}).RequireAuthorization(policy => policy.RequireRole("SupplierAdmin", "SupplierUser"));

// The supplier's quotation worksheet for one RFQ (.xlsx by default, ?format=csv for CSV).
app.MapGet("/supplier/rfqs/{rfqId:guid}/worksheet", async (Guid rfqId, string? format, HttpContext http, ProcurementService procurement) =>
{
    var supplierClaim = http.User.FindFirst("SupplierId")?.Value;
    if (supplierClaim is null) return Results.Forbid();
    var supplierId = Guid.Parse(supplierClaim);

    var rfq = await procurement.GetRfqForSupplierAsync(supplierId, rfqId);
    if (rfq is null) return Results.NotFound();

    var items = (await procurement.GetItemsByIdsAsync(rfq.Lines.Select(l => l.ItemId))).ToDictionary(i => i.Id);
    var quote = await procurement.GetQuoteAsync(rfqId, supplierId);
    var fileName = $"{rfq.RfqNumber}-quote-worksheet";

    if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
    {
        // UTF-8 with a BOM so Excel reads accented characters correctly.
        var utf8 = new System.Text.UTF8Encoding(true);
        var bytes = utf8.GetPreamble().Concat(utf8.GetBytes(QuoteSheet.BuildCsv(rfq, items, quote))).ToArray();
        return Results.File(bytes, "text/csv", fileName + ".csv");
    }

    return Results.File(QuoteSheet.BuildXlsx(rfq, items, quote),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName + ".xlsx");
}).RequireAuthorization(policy => policy.RequireRole("SupplierAdmin", "SupplierUser"));

// The formal purchase order as a PDF file: buyers get any of their POs (marked draft until approved), suppliers only their approved ones.
app.MapGet("/company/purchase-orders/{poId:guid}/pdf", async (Guid poId, HttpContext http, PoDocumentService documents) =>
{
    var companyClaim = http.User.FindFirst("CompanyId")?.Value;
    if (companyClaim is null) return Results.Forbid();

    var doc = await documents.GetForCompanyAsync(Guid.Parse(companyClaim), poId);
    if (doc is null) return Results.NotFound();
    return Results.File(PoPdf.Build(doc), "application/pdf", PoFileName(doc));
}).RequireAuthorization(policy => policy.RequireRole("CompanyAdmin", "CompanyUser"));

// The order as a ready-to-open email message for the sender's own mail program: to the supplier, the office in copy, PDF attached.
app.MapGet("/company/purchase-orders/{poId:guid}/email", async (Guid poId, HttpContext http, DispatchService dispatch) =>
{
    var companyClaim = http.User.FindFirst("CompanyId")?.Value;
    if (companyClaim is null) return Results.Forbid();
    var eml = await dispatch.BuildEmlAsync(Guid.Parse(companyClaim), poId);
    return eml is null ? Results.NotFound() : Results.File(eml.Value.Bytes, "message/rfc822", eml.Value.FileName);
}).RequireAuthorization(policy => policy.RequireRole("CompanyAdmin", "CompanyUser"));

app.MapGet("/supplier/purchase-orders/{poId:guid}/pdf", async (Guid poId, HttpContext http, PoDocumentService documents) =>
{
    var supplierClaim = http.User.FindFirst("SupplierId")?.Value;
    if (supplierClaim is null) return Results.Forbid();

    var doc = await documents.GetForSupplierAsync(Guid.Parse(supplierClaim), poId);
    if (doc is null) return Results.NotFound();
    return Results.File(PoPdf.Build(doc), "application/pdf", PoFileName(doc));
}).RequireAuthorization(policy => policy.RequireRole("SupplierAdmin", "SupplierUser"));

// Invoice supporting documents: only the buyer that owns the invoice, or the supplier that sent it, can download them.
// ?inline=true shows a PDF or image in the page (thumbnails, viewer); anything else, and every plain request, downloads.
app.MapGet("/files/invoices/{documentId:guid}", async (Guid documentId, bool? inline, HttpContext http, InvoiceService invoices) =>
{
    Guid? companyId = Guid.TryParse(http.User.FindFirst("CompanyId")?.Value, out var c) ? c : null;
    Guid? supplierId = Guid.TryParse(http.User.FindFirst("SupplierId")?.Value, out var s) ? s : null;

    var found = await invoices.OpenDocumentAsync(documentId, companyId, supplierId);
    if (found is null) return Results.NotFound();
    var doc = found.Value.Doc;
    if (inline == true && AttachmentRules.IsInlineSafe(doc.ContentType))
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (doc.ContentType != "application/pdf") http.Response.Headers["Content-Security-Policy"] = "sandbox";
        return Results.File(found.Value.Content, doc.ContentType);
    }
    return Results.File(found.Value.Content, doc.ContentType, doc.FileName);
}).RequireAuthorization();

// Requisition files: the company that owns them, or (when shared) a supplier invited to an RFQ made from the requisition.
// Served inline so images and PDFs open in the browser; sandboxed and nosniff so an uploaded file can never run as a page.
app.MapGet("/files/attachments/{attachmentId:guid}", async (Guid attachmentId, bool? download, HttpContext http, AttachmentService attachments) =>
{
    Guid? companyId = Guid.TryParse(http.User.FindFirst("CompanyId")?.Value, out var c) ? c : null;
    Guid? supplierId = Guid.TryParse(http.User.FindFirst("SupplierId")?.Value, out var s) ? s : null;

    var found = await attachments.OpenAsync(attachmentId, companyId, supplierId);
    if (found is null) return Results.NotFound();
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (found.Value.File.ContentType != "application/pdf") http.Response.Headers["Content-Security-Policy"] = "sandbox";
    return download == true
        ? Results.File(found.Value.Content, found.Value.File.ContentType, found.Value.File.FileName)
        : Results.File(found.Value.Content, found.Value.File.ContentType);
}).RequireAuthorization();

// This installation's permanent node id (shore by default, set Platform:Profile=Vessel onboard). Records are stamped with it.
static async Task EnsureNodeAsync(ChandleryDbContext db, IConfiguration config)
{
    var node = await db.NodeInfos.FirstOrDefaultAsync();
    if (node is null)
    {
        var profile = config["Platform:Profile"] ?? "Shore";
        // A vessel's id is the one the office registered for it, so a brand-new, empty database can start as that vessel.
        var registered = profile == "Vessel" && Guid.TryParseExact(config["SyncNode:NodeId"], "N", out var id) ? id : (Guid?)null;
        node = new NodeInfo
        {
            Id = registered ?? Guid.CreateVersion7(),
            Profile = profile,
            Name = config["Platform:NodeName"] ?? "Shore office",
            CreatedAt = DateTime.UtcNow
        };
        db.NodeInfos.Add(node);
        await db.SaveChangesAsync();
    }
    NodeContext.Set(node);
}

static string PoFileName(PoDocument doc) =>
    doc.Po.Status == PoStatus.Approved ? $"{doc.Po.PoNumber}.pdf" : $"{doc.Po.PoNumber}-DRAFT.pdf";

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
