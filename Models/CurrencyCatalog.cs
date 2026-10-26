namespace Chandlery.Models;

/// <summary>
/// The standard (ISO 4217) currencies the master is seeded with. Built in because the app runs without system culture data,
/// so names cannot be looked up at run time.
/// </summary>
public static class CurrencyCatalog
{
    /// <summary>The currencies every new company starts with.</summary>
    public static readonly string[] DefaultForCompany = { "USD", "EUR", "GBP", "SGD", "AED", "JPY", "CNY", "INR" };

    private static readonly string[] ZeroDecimal = { "BIF", "CLP", "DJF", "GNF", "ISK", "JPY", "KMF", "KRW", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
    private static readonly string[] ThreeDecimal = { "BHD", "IQD", "JOD", "KWD", "LYD", "OMR", "TND" };

    public static bool Contains(string? code) => !string.IsNullOrWhiteSpace(code) && Names.ContainsKey(code.Trim().ToUpperInvariant());

    public static int DecimalsFor(string code) =>
        ZeroDecimal.Contains(code) ? 0 : ThreeDecimal.Contains(code) ? 3 : 2;

    public static IEnumerable<Currency> All() =>
        Names.OrderBy(kv => kv.Key).Select(kv => new Currency { Code = kv.Key, Name = kv.Value, DecimalPlaces = DecimalsFor(kv.Key) });

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["AED"] = "UAE Dirham", ["AFN"] = "Afghan Afghani", ["ALL"] = "Albanian Lek", ["AMD"] = "Armenian Dram",
        ["ANG"] = "Netherlands Antillean Guilder", ["AOA"] = "Angolan Kwanza", ["ARS"] = "Argentine Peso", ["AUD"] = "Australian Dollar",
        ["AWG"] = "Aruban Florin", ["AZN"] = "Azerbaijani Manat", ["BAM"] = "Bosnia-Herzegovina Convertible Mark", ["BBD"] = "Barbadian Dollar",
        ["BDT"] = "Bangladeshi Taka", ["BGN"] = "Bulgarian Lev", ["BHD"] = "Bahraini Dinar", ["BIF"] = "Burundian Franc",
        ["BMD"] = "Bermudian Dollar", ["BND"] = "Brunei Dollar", ["BOB"] = "Bolivian Boliviano", ["BRL"] = "Brazilian Real",
        ["BSD"] = "Bahamian Dollar", ["BTN"] = "Bhutanese Ngultrum", ["BWP"] = "Botswana Pula", ["BYN"] = "Belarusian Ruble",
        ["BZD"] = "Belize Dollar", ["CAD"] = "Canadian Dollar", ["CDF"] = "Congolese Franc", ["CHF"] = "Swiss Franc",
        ["CLP"] = "Chilean Peso", ["CNY"] = "Chinese Yuan", ["COP"] = "Colombian Peso", ["CRC"] = "Costa Rican Colon",
        ["CUP"] = "Cuban Peso", ["CVE"] = "Cape Verdean Escudo", ["CZK"] = "Czech Koruna", ["DJF"] = "Djiboutian Franc",
        ["DKK"] = "Danish Krone", ["DOP"] = "Dominican Peso", ["DZD"] = "Algerian Dinar", ["EGP"] = "Egyptian Pound",
        ["ERN"] = "Eritrean Nakfa", ["ETB"] = "Ethiopian Birr", ["EUR"] = "Euro", ["FJD"] = "Fijian Dollar",
        ["FKP"] = "Falkland Islands Pound", ["FOK"] = "Faroese Krona", ["GBP"] = "Pound Sterling", ["GEL"] = "Georgian Lari",
        ["GGP"] = "Guernsey Pound", ["GHS"] = "Ghanaian Cedi", ["GIP"] = "Gibraltar Pound", ["GMD"] = "Gambian Dalasi",
        ["GNF"] = "Guinean Franc", ["GTQ"] = "Guatemalan Quetzal", ["GYD"] = "Guyanese Dollar", ["HKD"] = "Hong Kong Dollar",
        ["HNL"] = "Honduran Lempira", ["HRK"] = "Croatian Kuna", ["HTG"] = "Haitian Gourde", ["HUF"] = "Hungarian Forint",
        ["IDR"] = "Indonesian Rupiah", ["ILS"] = "Israeli New Shekel", ["IMP"] = "Manx Pound", ["INR"] = "Indian Rupee",
        ["IQD"] = "Iraqi Dinar", ["IRR"] = "Iranian Rial", ["ISK"] = "Icelandic Krona", ["JEP"] = "Jersey Pound",
        ["JMD"] = "Jamaican Dollar", ["JOD"] = "Jordanian Dinar", ["JPY"] = "Japanese Yen", ["KES"] = "Kenyan Shilling",
        ["KGS"] = "Kyrgyzstani Som", ["KHR"] = "Cambodian Riel", ["KID"] = "Kiribati Dollar", ["KMF"] = "Comorian Franc",
        ["KRW"] = "South Korean Won", ["KWD"] = "Kuwaiti Dinar", ["KYD"] = "Cayman Islands Dollar", ["KZT"] = "Kazakhstani Tenge",
        ["LAK"] = "Lao Kip", ["LBP"] = "Lebanese Pound", ["LKR"] = "Sri Lankan Rupee", ["LRD"] = "Liberian Dollar",
        ["LSL"] = "Lesotho Loti", ["LYD"] = "Libyan Dinar", ["MAD"] = "Moroccan Dirham", ["MDL"] = "Moldovan Leu",
        ["MGA"] = "Malagasy Ariary", ["MKD"] = "Macedonian Denar", ["MMK"] = "Myanmar Kyat", ["MNT"] = "Mongolian Tugrik",
        ["MOP"] = "Macanese Pataca", ["MRU"] = "Mauritanian Ouguiya", ["MUR"] = "Mauritian Rupee", ["MVR"] = "Maldivian Rufiyaa",
        ["MWK"] = "Malawian Kwacha", ["MXN"] = "Mexican Peso", ["MYR"] = "Malaysian Ringgit", ["MZN"] = "Mozambican Metical",
        ["NAD"] = "Namibian Dollar", ["NGN"] = "Nigerian Naira", ["NIO"] = "Nicaraguan Cordoba", ["NOK"] = "Norwegian Krone",
        ["NPR"] = "Nepalese Rupee", ["NZD"] = "New Zealand Dollar", ["OMR"] = "Omani Rial", ["PAB"] = "Panamanian Balboa",
        ["PEN"] = "Peruvian Sol", ["PGK"] = "Papua New Guinean Kina", ["PHP"] = "Philippine Peso", ["PKR"] = "Pakistani Rupee",
        ["PLN"] = "Polish Zloty", ["PYG"] = "Paraguayan Guarani", ["QAR"] = "Qatari Riyal", ["RON"] = "Romanian Leu",
        ["RSD"] = "Serbian Dinar", ["RUB"] = "Russian Ruble", ["RWF"] = "Rwandan Franc", ["SAR"] = "Saudi Riyal",
        ["SBD"] = "Solomon Islands Dollar", ["SCR"] = "Seychellois Rupee", ["SDG"] = "Sudanese Pound", ["SEK"] = "Swedish Krona",
        ["SGD"] = "Singapore Dollar", ["SHP"] = "Saint Helena Pound", ["SLE"] = "Sierra Leonean Leone", ["SOS"] = "Somali Shilling",
        ["SRD"] = "Surinamese Dollar", ["SSP"] = "South Sudanese Pound", ["STN"] = "Sao Tome and Principe Dobra", ["SYP"] = "Syrian Pound",
        ["SZL"] = "Eswatini Lilangeni", ["THB"] = "Thai Baht", ["TJS"] = "Tajikistani Somoni", ["TMT"] = "Turkmenistan Manat",
        ["TND"] = "Tunisian Dinar", ["TOP"] = "Tongan Paanga", ["TRY"] = "Turkish Lira", ["TTD"] = "Trinidad and Tobago Dollar",
        ["TVD"] = "Tuvaluan Dollar", ["TWD"] = "New Taiwan Dollar", ["TZS"] = "Tanzanian Shilling", ["UAH"] = "Ukrainian Hryvnia",
        ["UGX"] = "Ugandan Shilling", ["USD"] = "US Dollar", ["UYU"] = "Uruguayan Peso", ["UZS"] = "Uzbekistani Som",
        ["VES"] = "Venezuelan Bolivar", ["VND"] = "Vietnamese Dong", ["VUV"] = "Vanuatu Vatu", ["WST"] = "Samoan Tala",
        ["XAF"] = "Central African CFA Franc", ["XCD"] = "East Caribbean Dollar", ["XCG"] = "Caribbean Guilder", ["XDR"] = "IMF Special Drawing Rights",
        ["XOF"] = "West African CFA Franc", ["XPF"] = "CFP Franc", ["YER"] = "Yemeni Rial", ["ZAR"] = "South African Rand",
        ["ZMW"] = "Zambian Kwacha", ["ZWG"] = "Zimbabwe Gold", ["ZWL"] = "Zimbabwean Dollar",
    };
}
