using System.Reflection;
using System.Runtime.Serialization;
using System.ServiceModel;

namespace Konta.EFactura.Tests;

/// <summary>
/// What can be checked without reaching SFS: the binding the guide describes, the published
/// addresses, and that the generated contract is the test service's WSDL, operation for operation
/// and member for member, and how production's differs.
/// </summary>
public sealed class ClientTests
{
    [Fact]
    public void The_binding_is_https_with_the_user_name_in_the_message()
    {
        var binding = EFacturaClient.Binding(TimeSpan.FromSeconds(90));

        Assert.Equal(BasicHttpSecurityMode.TransportWithMessageCredential, binding.Security.Mode);
        Assert.Equal(BasicHttpMessageCredentialType.UserName, binding.Security.Message.ClientCredentialType);
        Assert.Equal(EFacturaClient.MaxMessageBytes, binding.MaxReceivedMessageSize);
        Assert.Equal(TimeSpan.FromSeconds(90), binding.SendTimeout);
    }

    [Theory]
    [InlineData(EFacturaEnvironment.Test, "https://apiefactura-pre.sfs.md/Service.svc")]
    [InlineData(EFacturaEnvironment.Production, "https://efactura-api.sfs.md/Service.svc")]
    public async Task A_client_calls_the_address_of_its_environment(EFacturaEnvironment environment, string address)
    {
        await using var client = new EFacturaClient(environment, "api-user", "not-a-real-password");

        Assert.Equal(new Uri(address), client.Address);
    }

    [Fact]
    public void A_client_needs_a_user_and_a_password()
    {
        Assert.Throws<ArgumentException>(() => new EFacturaClient(EFacturaEnvironment.Test, " ", "x"));
        Assert.Throws<ArgumentException>(() => new EFacturaClient(EFacturaEnvironment.Test, "user", ""));
    }

    [Fact]
    public void The_generated_contract_has_every_operation_of_the_wsdl()
    {
        var published = Wsdl.Operations(Wsdl.Committed("Service.wsdl"));

        var generated = typeof(Generated.IService).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(OperationContractAttribute), false).Length > 0)
            .Select(m => m.Name.EndsWith("Async", StringComparison.Ordinal) ? m.Name[..^5] : m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(20, published.Count);
        Assert.Equal(published, generated);
    }

    [Fact]
    public void The_generated_contract_has_every_member_of_the_wsdl()
    {
        // The test service's WSDL of 2026-10-22 added members to existing types (InvoiceIds, Id), which
        // a comparison of operations alone would not see.
        var published = Wsdl.Members(Wsdl.Committed("Service.wsdl")).Select(m => m[..m.IndexOf(':', StringComparison.Ordinal)]).ToList();

        var generated = typeof(Generated.IService).Assembly.GetTypes()
            .Select(t => (Type: t, Contract: t.GetCustomAttributes(typeof(DataContractAttribute), false).OfType<DataContractAttribute>().SingleOrDefault()))
            .Where(t => t.Contract?.Namespace == Wsdl.Model)
            .SelectMany(t => t.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(p => p.GetCustomAttributes(typeof(DataMemberAttribute), false).Length > 0)
                .Select(p => $"{t.Contract!.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(published, generated);
    }

    [Fact]
    public void Production_lacks_only_what_the_test_service_added()
    {
        // The contract is generated from the test service's WSDL, which was ahead of production's on
        // 2026-10-22. When production catches up, this fails: replace Service-production.wsdl and
        // drop what it then has from the lists below.
        var test = Wsdl.Shape(Wsdl.Committed("Service.wsdl"));
        var production = Wsdl.Shape(Wsdl.Committed("Service-production.wsdl"));

        Assert.Equal(
            [
                "member DeleteInvoiceRequest.Id: guid",
                "member DeleteInvoiceResponse.Message: string",
                "member InvoiceIndentificator.Id: string",
                "member PostInvocesResponse.InvoiceIds: ArrayOfstring",
                "operation DeleteDraftInvoice",
            ],
            test.Except(production).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "member Attachment.FileContent: string",
                "member Attachment.FileName: string",
                "member PostInvocesRequest.Attachment: Attachment",
            ],
            production.Except(test).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_execution_error_is_raised_with_the_platforms_message()
    {
        var answer = new Generated.PostInvocesResponse
        {
            RequestId = "r-1",
            Status = 3,
            ErrorMessage = "Validation failed: \tValidation error: The element 'Documents' has incomplete content.",
        };

        var error = Assert.Throws<EFacturaException>(() => EFacturaClient.Checked("PostInvoices", answer, r => r.ErrorMessage));

        Assert.Equal("PostInvoices", error.Operation);
        Assert.Equal("r-1", error.RequestId);
        Assert.StartsWith("Validation failed", error.PlatformMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)] // GetTaxpayersInfo answers 1 even when it found everyone
    [InlineData(2)]
    public void Accepted_and_done_are_answers(int status)
    {
        var answer = new Generated.BaseResponse { Status = status };

        Assert.Same(answer, EFacturaClient.Checked("Test", answer));
    }

    [Fact]
    public void A_refused_invoice_is_read_out_of_the_error_message()
    {
        // As the test service sent it on 2026-10-08, with Status 2 and TotalInvoicesPosted 0.
        const string message = "Invoice order = 0, error: Autorul facturii nu este desemnat drept furnizor.\n\n";

        var refusal = Assert.Single(EFacturaPostResult.ParseRefusals(message));

        Assert.Equal(new EFacturaRefusal(0, "Autorul facturii nu este desemnat drept furnizor."), refusal);
    }

    [Fact]
    public void Each_refused_invoice_of_a_batch_is_read_separately()
    {
        const string message = "Invoice order = 0, error: Primul motiv.\n\nInvoice order = 2, error: Al doilea\nmotiv.\n\n";

        Assert.Equal(
            [new EFacturaRefusal(0, "Primul motiv."), new EFacturaRefusal(2, "Al doilea\nmotiv.")],
            EFacturaPostResult.ParseRefusals(message));
    }

    [Fact]
    public void A_batch_is_posted_only_when_every_invoice_is()
    {
        Assert.True(new EFacturaPostResult("r", 2, 2, [], null, []).AllPosted);
        Assert.False(new EFacturaPostResult("r", 2, 1, [new EFacturaRefusal(1, "x")], "x", []).AllPosted);
        Assert.False(new EFacturaPostResult("r", 0, 0, [], null, []).AllPosted);
    }

    [Fact]
    public void A_draft_is_known_by_our_id_in_its_xml()
    {
        // The shape GetInvoicesForSigning returned on 2026-10-08, with our id in AdditionalInformation.
        const string xml = "<Document><SupplierInfo><DeliveryDate>2026-10-08T14:22:27.015Z</DeliveryDate></SupplierInfo>"
            + "<AdditionalInformation><id>konta-distinct-12</id></AdditionalInformation></Document>";

        var draft = new EFacturaInvoiceXml(new EFacturaInvoiceId("", ""), true, EFacturaInvoiceStatus.Draft, null, xml);

        Assert.Equal("konta-distinct-12", draft.CorrelationId);
        Assert.Null((draft with { Xml = null }).CorrelationId);
    }
}
