using System.Text.Json.Nodes;
using Chandlery.Services;

namespace Chandlery.Sync.Tests;

/// <summary>
/// The legacy-form converter on three small forms built to the legacy markup as far as it is known (tables, <c>data-ititle="Label [Section]"</c>, <c>txtboxcol</c> and
/// <c>cDatePicker</c> inputs, the hidden <c>hidxml</c>/<c>hidhtml</c> boxes): an engine report with a repeated header, a checklist and a handover form.
/// </summary>
public class FormConverterTests
{
    internal const string EngineReport = """
        <html><head><title>Crankpin Bearings</title><script>function calc(){ return 1; }</script></head><body class="form-details">
        <input type="hidden" id="hidxml" value=""><input type="hidden" id="hidhtml" value="">
        <table id="tblVessel">
          <tr><td>Engine type</td><td><input type="text" class="txtboxcol" id="Engine_type_Vessel" data-ititle="Engine type [Vessel]" value="7G60M-CGIE-HPSCR"></td>
              <td>Date</td><td><input type="text" class="cDatePicker hasDatepicker" data-date-format="dd/mm/yyyy" data-ititle="Date [Vessel]" value="04-11-2026"></td></tr>
          <tr><td>Place</td><td><input type="text" class="txtboxcol" data-ititle="Place [Vessel]" value="USA DRIFTING"></td>
              <td>Condition</td><td><select data-ititle="Condition [Vessel]"><option value=""></option><option value="Good">Good</option><option value="Bad" selected>Bad</option></select></td></tr>
          <tr><td>Remarks</td><td colspan="3"><textarea data-ititle="Remarks [Vessel]">Bearing two scored</textarea></td></tr>
        </table>
        <table id="tblMeasured">
          <tr><th rowspan="2">Position</th><th colspan="2">Bearing 1</th><th colspan="2">Bearing 2</th></tr>
          <tr><th>F</th><th>A</th><th>F</th><th>A</th></tr>
          <tr><td>Original</td><td><input class="txtboxcol2" data-ititle="Original B1 F [Measured]" value="0.50"></td><td><input class="txtboxcol2" data-ititle="Original B1 A [Measured]" value="0.45"></td>
              <td><input class="txtboxcol2" data-ititle="Original B2 F [Measured]" value="0.50"></td><td><input class="txtboxcol2" data-ititle="Original B2 A [Measured]" value="0.44"></td></tr>
          <tr><td>Measured</td><td><input class="txtboxcol2" data-ititle="Measured B1 F [Measured]" value="0.55"></td><td><input class="txtboxcol2" data-ititle="Measured B1 A [Measured]" value=""></td>
              <td><input class="txtboxcol2" data-ititle="Measured B2 F [Measured]" value="0.52"></td><td><input class="txtboxcol2" data-ititle="Measured B2 A [Measured]" value="0.49"></td></tr>
        </table>
        </body></html>
        """;

    private const string Checklist = """
        <html><head><title>Lifesaving Appliances</title></head><body>
        <table id="tblList">
          <tr><th>Item</th><th>Status</th><th>Remarks</th></tr>
          <tr><td>Lifeboat davits</td><td><select data-ititle="Lifeboat davits Status [Checks]"><option></option><option>Yes</option><option>No</option><option>NA</option></select></td><td><input data-ititle="Lifeboat davits Remarks [Checks]" value=""></td></tr>
          <tr><td>Liferafts</td><td><select data-ititle="Liferafts Status [Checks]"><option></option><option>Yes</option><option selected>No</option><option>NA</option></select></td><td><input data-ititle="Liferafts Remarks [Checks]" value="Service due"></td></tr>
          <tr><td>Immersion suits</td><td><select data-ititle="Immersion suits Status [Checks]"><option></option><option selected>Yes</option><option>No</option><option>NA</option></select></td><td><input data-ititle="Immersion suits Remarks [Checks]" value=""></td></tr>
        </table></body></html>
        """;

    private const string Handover = """
        <html><head><title>Change of Command</title></head><body>
        <h1>Change of command</h1>
        <table id="tblHandover">
          <tr><td>Outgoing master</td><td><input data-ititle="Outgoing master [Handover]" value="A. Jones"></td></tr>
          <tr><td>Incoming master</td><td><input data-ititle="Incoming master [Handover]" value="B. Smith"></td></tr>
          <tr><td>Date of handover</td><td><input class="cDatePicker" data-ititle="Date of handover [Handover]" value="12/11/2026"></td></tr>
          <tr><td>Cash on board</td><td><input class="txtboxcol" data-ititle="Cash on board [Handover]" value="2500.50"></td></tr>
          <tr><td>Remarks</td><td><textarea data-ititle="Remarks [Handover]">All handed over.</textarea></td></tr>
          <tr><td>Remarks</td><td><textarea data-ititle="Remarks [Notes]"></textarea></td></tr>
        </table></body></html>
        """;

    private static FormConversion Convert(string html) => FormHtmlConverter.Convert(html);

    private static decimal Num(JsonNode? node) => node!.GetValue<decimal>();

    [Fact]
    public void AnEngineReportBecomesASectionOfFieldsAndAGridWithARepeatedColumnGroup()
    {
        var c = Convert(EngineReport);
        Assert.Equal("Crankpin Bearings", c.Definition.Title);
        Assert.Equal(["tblVessel", "tblMeasured"], c.Definition.Sections.Select(s => s.Id));

        var header = c.Definition.Sections[0];
        Assert.Equal(("fields", "Vessel"), (header.Layout, header.Title));
        Assert.Equal(["engineType", "date", "place", "condition", "remarks"], header.Fields!.Select(f => f.Id));
        Assert.Equal([FormFieldTypes.Text, FormFieldTypes.Date, FormFieldTypes.Text, FormFieldTypes.Choice, FormFieldTypes.LongText], header.Fields!.Select(f => f.Type));
        Assert.Equal(["Good", "Bad"], header.Fields![3].Options);
        Assert.Equal("Engine type", header.Fields![0].Label);

        var grid = c.Definition.Sections[1];
        Assert.Equal("grid", grid.Layout);
        Assert.Equal(["original", "measured"], grid.Rows!.Items!.Select(r => r.Id));
        Assert.Equal(["Original", "Measured"], grid.Rows.Items!.Select(r => r.Label));
        var group = Assert.Single(grid.Columns!);
        Assert.Equal(("bearing", 2), (group.Id, group.Repeat));
        Assert.Equal(["f", "a"], group.Children!.Select(x => x.Id));
        Assert.All(group.Children!, x => Assert.Equal(FormFieldTypes.Number, x.Type));                  // every answer in the column is a number
        Assert.Equal(["bearing1.f", "bearing1.a", "bearing2.f", "bearing2.a"], FormDef.Cells(grid).Select(x => x.Id));
    }

    [Fact]
    public void TheAnswersInTheFileAreKept_ByTheSameIdsTheDraftUses()
    {
        var c = Convert(EngineReport);
        Assert.Equal("7G60M-CGIE-HPSCR", c.Values["engineType"]!.ToString());
        Assert.Equal("2026-11-04", c.Values["date"]!.ToString());                                        // dd-MM-yyyy read, kept as a date
        Assert.Equal("Bad", c.Values["condition"]!.ToString());
        Assert.Equal("Bearing two scored", c.Values["remarks"]!.ToString());
        var measured = (JsonObject)c.Values["tblMeasured"]!;
        Assert.Equal(0.50m, Num(measured["original"]!["bearing1.f"]));
        Assert.Equal(0.44m, Num(measured["original"]!["bearing2.a"]));
        Assert.Equal(0.55m, Num(measured["measured"]!["bearing1.f"]));
        Assert.Null(measured["measured"]!["bearing1.a"]);                                                 // an empty box is no answer

        // The draft renders and accepts what the old form held: nothing is dropped.
        var check = FormValueRules.Check(c.Definition, c.Values, new DateOnly(2026, 11, 15), forSubmit: false);
        Assert.True(check.Ok);
        Assert.Equal(c.Values.ToJsonString(), check.Clean.ToJsonString());
    }

    [Fact]
    public void TheDraftIsValidAndSurvivesItsOwnJson_AndTheReportSaysWhatItCouldNotDo()
    {
        var c = Convert(EngineReport);
        Assert.Empty(FormDefinitionRules.Validate(c.Definition));
        Assert.DoesNotContain(c.Notes, n => n.Message.StartsWith("The draft breaks a rule"));
        var again = FormDef.TryRead(c.Json, out var error);
        Assert.True(again is not null, error);
        Assert.Equal(c.Json, again!.ToPrettyJson());
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Check && n.Where == "scripts" && n.Message.Contains("1 script"));      // not run, not converted
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Info && n.Message.Contains("repeats 2 times"));
        Assert.DoesNotContain(c.Notes, n => n.Level == ConversionLevel.Skipped);                                                          // the two hidden boxes are not inputs
        Assert.Equal((13, 13), (c.Inputs, c.Converted - 0));                                                                              // 5 fields + 2 rows x 4 cells = 13
    }

    [Fact]
    public void AChecklistBecomesAGridOfRowsWithAChoiceColumn()
    {
        var c = Convert(Checklist);
        var grid = Assert.Single(c.Definition.Sections);
        Assert.Equal(("tblList", "grid", "Checks"), (grid.Id, grid.Layout, grid.Title));
        Assert.Equal(["lifeboatDavits", "liferafts", "immersionSuits"], grid.Rows!.Items!.Select(r => r.Id));
        Assert.Equal(["status", "remarks"], grid.Columns!.Select(x => x.Id));
        Assert.Equal([FormFieldTypes.Choice, FormFieldTypes.Text], grid.Columns!.Select(x => x.Type));
        Assert.Equal(["Yes", "No", "NA"], grid.Columns![0].Options);
        Assert.Equal("No", c.Values["tblList"]!["liferafts"]!["status"]!.ToString());
        Assert.Equal("Service due", c.Values["tblList"]!["liferafts"]!["remarks"]!.ToString());
        Assert.Null(c.Values["tblList"]!["lifeboatDavits"]);                                              // nothing was answered on that row
        Assert.Empty(FormDefinitionRules.Validate(c.Definition));
    }

    [Fact]
    public void AHandoverFormWithOneInputPerRowIsFields_NumbersAndDatesAreRead_AndSameNamedFieldsGetOwnIds()
    {
        var c = Convert(Handover);
        var section = Assert.Single(c.Definition.Sections);
        Assert.Equal("fields", section.Layout);
        Assert.Equal(["outgoingMaster", "incomingMaster", "dateOfHandover", "cashOnBoard", "remarks", "remarks2"], section.Fields!.Select(f => f.Id));
        Assert.Equal(FormFieldTypes.Date, section.Fields![2].Type);
        Assert.Equal("2026-11-12", c.Values["dateOfHandover"]!.ToString());
        Assert.Equal(FormFieldTypes.Number, section.Fields![3].Type);                                    // guessed from the value, and said so
        Assert.Equal(2500.50m, Num(c.Values["cashOnBoard"]));
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Check && n.Where == "Cash on board [Handover]" && n.Message.Contains("Check that it is not text"));
        Assert.Equal("Change of Command", c.Definition.Title);                                          // the page title
        Assert.Empty(FormDefinitionRules.Validate(c.Definition));
    }

    [Fact]
    public void TwoFieldsSideBySideOnEachRowIsNotMistakenForAGrid()
    {
        var c = Convert("""
            <table id="t"><tr><td>A</td><td><input data-ititle="Alpha [S]" value="1x"></td><td>B</td><td><input data-ititle="Beta [S]" value="2x"></td></tr>
                           <tr><td>C</td><td><input data-ititle="Gamma [S]"></td><td>D</td><td><input data-ititle="Delta [S]"></td></tr></table>
            """);
        Assert.Equal("fields", Assert.Single(c.Definition.Sections).Layout);
        Assert.Equal(4, c.Definition.Sections[0].Fields!.Count);
    }

    [Fact]
    public void WhatCannotBeReadIsReported_NotGuessedAtOrDropped_WithoutSaying()
    {
        var c = Convert("""
            <table id="t">
              <tr><td>Name</td><td><input data-ititle="Name [S]" value="x"></td></tr>
              <tr><td>Code</td><td><input data-ititle="Code [S]" value="007"></td></tr>
              <tr><td>Day</td><td><input class="cDatePicker" data-ititle="Day [S]" value="not a date"></td></tr>
              <tr><td>Total</td><td><input data-ititle="Total [S]" value="5" readonly></td></tr>
              <tr><td>Loose</td><td><input id="loose_box" value="y"></td></tr>
              <tr><td>Pick</td><td><input type="radio" data-ititle="Pick [S]" value="1"></td></tr>
              <tr><td></td><td><input></td></tr>
            </table>
            """);
        var fields = c.Definition.Sections[0].Fields!;
        Assert.Equal(["name", "code", "day", "total", "loose"], fields.Select(f => f.Id));
        Assert.Equal(FormFieldTypes.Text, fields[1].Type);                                               // 007 is a code, not a number
        Assert.Null(c.Values["day"]);
        Assert.Contains(c.Notes, n => n.Where == "Day [S]" && n.Message.Contains("not a valid date"));
        Assert.Contains(c.Notes, n => n.Where == "Total [S]" && n.Message.Contains("read-only"));
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Check && n.Where == "Loose" && n.Message.Contains("No data-ititle"));
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Skipped && n.Message.Contains("radio"));
        Assert.Contains(c.Notes, n => n.Level == ConversionLevel.Skipped && n.Message.Contains("no data-ititle, id or label"));
    }

    [Fact]
    public void AnEmptyOrHugeFileOrOneWithNoInputsGivesAnEmptyDraftAndSaysSo()
    {
        Assert.Contains(Convert("  ").Notes, n => n.Level == ConversionLevel.Skipped && n.Message.Contains("empty"));
        Assert.Contains(Convert(new string('x', FormHtmlConverter.MaxHtmlChars + 1)).Notes, n => n.Level == ConversionLevel.Skipped && n.Message.Contains("larger"));
        var none = Convert("<html><body><p>No inputs</p></body></html>");
        Assert.Empty(none.Definition.Sections);
        Assert.Equal(0, none.Inputs);
    }

    [Fact]
    public void ScriptsInTheFileAreNeverRun()
    {
        var c = Convert("""<html><body><script>throw new Error('x');</script><table id="t"><tr><td><input data-ititle="A [S]" value="1" onkeyup="calc()"></td></tr></table></body></html>""");
        Assert.Contains(c.Notes, n => n.Where == "scripts" && n.Message.Contains("script handler"));
        Assert.Single(c.Definition.Sections[0].Fields!);
    }
}
