using System.ServiceModel;
using System.Xml.Linq;

namespace Konta.EFactura.Tests;

/// <summary>
/// What can be checked without reaching SFS: the binding the guide describes, the published
/// addresses, and that the generated contract is the WSDL's, operation for operation.
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
        XNamespace wsdl = "http://schemas.xmlsoap.org/wsdl/";
        var published = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "sfs", "Service.wsdl"))
            .Descendants(wsdl + "portType")
            .Elements(wsdl + "operation")
            .Select(o => (string)o.Attribute("name")!)
            .Order(StringComparer.Ordinal)
            .ToList();

        var generated = typeof(Generated.IService).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(OperationContractAttribute), false).Length > 0)
            .Select(m => m.Name.EndsWith("Async", StringComparison.Ordinal) ? m.Name[..^5] : m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(19, published.Count);
        Assert.Equal(published, generated);
    }
}
