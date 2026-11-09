using Chandlery.Services;
using Xunit;

namespace Chandlery.Sync.Tests;

/// <summary>The dashboard's module cards and attention strip: what makes a card red or amber, and what reaches the strip.</summary>
public class DashboardModulesTests
{
    private static ModuleStat Stat(string label, string value, string href, string tone = "accent", string? hint = null, int count = 0, string? text = null) => new(label, value, href, tone, hint, count, text);

    private static ModuleSummary Card(params ModuleStat[] stats) => new("m", "Module", "/m", [.. stats]);

    [Fact]
    public void A_card_with_nothing_to_look_at_is_clear()
    {
        var card = Card(Stat("Overdue", "0", "/x", "success", null, 0, "things overdue"), Stat("Open", "4", "/y", "accent"));
        Assert.Equal(0, card.Severity);
        Assert.Empty(DashboardModuleService.Attention([card]));
    }

    [Fact]
    public void The_worst_number_decides_the_card_and_worst_items_come_first_in_the_strip()
    {
        var quiet = Card(Stat("Waiting", "2", "/w", "warning", null, 2, "tasks waiting"));
        var loud = Card(Stat("Late", "3", "/l", "danger", null, 3, "jobs overdue"), Stat("Info", "9", "/i", "info"));

        Assert.Equal(1, quiet.Severity);
        Assert.Equal(2, loud.Severity);

        var strip = DashboardModuleService.Attention([quiet, loud]);
        Assert.Equal(["jobs overdue", "tasks waiting"], strip.Select(i => i.Text));
        Assert.Equal([true, false], strip.Select(i => i.Urgent));
        Assert.Equal([3, 2], strip.Select(i => i.Count));
    }

    [Fact]
    public void A_number_without_attention_text_colours_the_card_but_stays_out_of_the_strip()
    {
        var card = Card(Stat("Due in 7 days", "5", "/p", "warning"));
        Assert.Equal(1, card.Severity);
        Assert.Empty(DashboardModuleService.Attention([card]));
    }

    [Fact]
    public void Attention_counts_the_flagged_figure_not_the_displayed_one()
    {
        // The card shows RFQs awaiting quotes (3) but flags the 2 that have quotes to compare.
        var card = Card(Stat("RFQs awaiting quotes", "3", "/rfqs", "info", "2 with quotes to compare", 2, "RFQs with quotes to compare"));
        var item = Assert.Single(DashboardModuleService.Attention([card]));
        Assert.Equal(2, item.Count);
        Assert.False(item.Urgent);
        Assert.Equal(1, card.Severity);
    }

    [Fact]
    public void The_purchasing_card_flags_approvals_as_urgent()
    {
        var c = new CompanyDashboard(OpenRequisitions: 4, RfqsAwaitingQuotes: 3, RfqsWithQuotes: 1, PendingMyApproval: 2, AwaitingDelivery: 6,
            SpendCurrency: "USD", Spend30Days: 0, MyApprovals: [], QuotesToCompare: [], RequisitionsWithoutRfq: [], TopSuppliers: [], Activity: [],
            MyInvoiceApprovals: [], InvoicesToVerify: [], InvoicesToFinalize: 0);
        var card = DashboardModuleService.Procurement(c);

        Assert.Equal(5, card.Stats.Count);
        Assert.Equal(2, card.Severity);
        var strip = DashboardModuleService.Attention([card]);
        Assert.Equal(["waiting for your approval", "RFQs with quotes to compare"], strip.Select(i => i.Text));
        Assert.Equal("—", card.Stats[^1].Value);   // no spend in 30 days shows a dash, not zero money
    }
}
