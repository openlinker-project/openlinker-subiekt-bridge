namespace SferaApi.Validation;

// FluentValidation validators are stateless and thread-safe, so a single shared
// instance per type is reused across requests. Endpoint modules reference these
// via their resource-scoped aliases (ProductValidators.Create, etc.) keeping the
// validate-then-400 gate call-site uniform.
public static class ProductValidators
{
    public static readonly CreateTowarRequestValidator Create = new();
}

public static class CustomerValidators
{
    public static readonly CreateFirmaRequestValidator Upsert = new();
}

public static class InvoiceValidators
{
    public static readonly CreateInvoiceRequestValidator Create = new();
    public static readonly KorektaRequestValidator Korekta = new();
}
