namespace Chandlery.Services;

/// <summary>The kinds of record the app can link to.</summary>
public enum DocKind
{
    Requisition,
    Rfq,
    PurchaseOrder,
    DeliveryOrder,
    Invoice,
    Supplier,
    Vessel,
    Item,
    Department
}

/// <summary>One place that knows where each kind of record lives.</summary>
public static class DocLinks
{
    public static string For(DocKind kind, int id, bool supplierPortal = false) => kind switch
    {
        DocKind.Requisition => $"/company/requisitions/{id}",
        DocKind.Rfq => supplierPortal ? $"/supplier/rfqs/{id}" : $"/company/rfqs/{id}",
        DocKind.PurchaseOrder => supplierPortal ? "/supplier/purchase-orders" : $"/company/purchase-orders/{id}",
        DocKind.DeliveryOrder => $"/company/purchase-orders/{id}",
        DocKind.Invoice => supplierPortal ? $"/supplier/invoices/{id}" : $"/company/invoices/{id}",
        DocKind.Supplier => $"/company/suppliers/{id}",
        DocKind.Vessel => $"/company/vessels/{id}",
        DocKind.Item => $"/company/items/{id}",
        DocKind.Department => $"/company/departments/{id}",
        _ => "/"
    };
}
