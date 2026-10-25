using System.Security.Claims;
using Chandlery.Components;
using Chandlery.Data;
using Chandlery.Models;
using Chandlery.Services;
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

builder.Services.AddScoped<ProcurementService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<RequisitionService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<HubService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddScoped<PoDocumentService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddSingleton<IFileStore, DiskFileStore>();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
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
    await DbSeeder.SeedAsync(db);
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

static List<Claim> BuildClaims(AppUser user)
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
    return claims;
}

static string DefaultHomeFor(UserRole role) => "/";

app.MapPost("/account/login", async (HttpContext http, ProcurementService procurement, IAntiforgery antiforgery) =>
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

    var identity = new ClaimsIdentity(BuildClaims(user), CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    var defaultHome = DefaultHomeFor(user.Role);
    var redirectTo = string.IsNullOrWhiteSpace(returnUrl) || returnUrl == "/" ? defaultHome : returnUrl;
    return Results.Redirect(redirectTo);
});

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
    var result = await procurement.GetSupplierRfqPageAsync(int.Parse(supplierClaim), selected, q, 1, int.MaxValue);

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
app.MapGet("/supplier/rfqs/{rfqId:int}/worksheet", async (int rfqId, string? format, HttpContext http, ProcurementService procurement) =>
{
    var supplierClaim = http.User.FindFirst("SupplierId")?.Value;
    if (supplierClaim is null) return Results.Forbid();
    var supplierId = int.Parse(supplierClaim);

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
app.MapGet("/company/purchase-orders/{poId:int}/pdf", async (int poId, HttpContext http, PoDocumentService documents) =>
{
    var companyClaim = http.User.FindFirst("CompanyId")?.Value;
    if (companyClaim is null) return Results.Forbid();

    var doc = await documents.GetForCompanyAsync(int.Parse(companyClaim), poId);
    if (doc is null) return Results.NotFound();
    return Results.File(PoPdf.Build(doc), "application/pdf", PoFileName(doc));
}).RequireAuthorization(policy => policy.RequireRole("CompanyAdmin", "CompanyUser"));

app.MapGet("/supplier/purchase-orders/{poId:int}/pdf", async (int poId, HttpContext http, PoDocumentService documents) =>
{
    var supplierClaim = http.User.FindFirst("SupplierId")?.Value;
    if (supplierClaim is null) return Results.Forbid();

    var doc = await documents.GetForSupplierAsync(int.Parse(supplierClaim), poId);
    if (doc is null) return Results.NotFound();
    return Results.File(PoPdf.Build(doc), "application/pdf", PoFileName(doc));
}).RequireAuthorization(policy => policy.RequireRole("SupplierAdmin", "SupplierUser"));

// Invoice supporting documents: only the buyer that owns the invoice, or the supplier that sent it, can download them.
app.MapGet("/files/invoices/{documentId:int}", async (int documentId, HttpContext http, InvoiceService invoices) =>
{
    int? companyId = int.TryParse(http.User.FindFirst("CompanyId")?.Value, out var c) ? c : null;
    int? supplierId = int.TryParse(http.User.FindFirst("SupplierId")?.Value, out var s) ? s : null;

    var found = await invoices.OpenDocumentAsync(documentId, companyId, supplierId);
    if (found is null) return Results.NotFound();
    return Results.File(found.Value.Content, found.Value.Doc.ContentType, found.Value.Doc.FileName);
}).RequireAuthorization();

static string PoFileName(PoDocument doc) =>
    doc.Po.Status == PoStatus.Approved ? $"{doc.Po.PoNumber}.pdf" : $"{doc.Po.PoNumber}-DRAFT.pdf";

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
