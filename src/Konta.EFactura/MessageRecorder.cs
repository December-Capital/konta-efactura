using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Dispatcher;

namespace Konta.EFactura;

/// <summary>
/// Hands each SOAP message to a recorder as it leaves and as the answer arrives, so the live tests
/// can keep request and response fixtures (rule 6). Sees the message before WCF adds the security
/// header, so the password is never in what is recorded.
/// </summary>
internal sealed class MessageRecorder(Action<string, string> record) : IEndpointBehavior, IClientMessageInspector
{
    public void AddBindingParameters(ServiceEndpoint endpoint, BindingParameterCollection bindingParameters)
    {
    }

    public void ApplyClientBehavior(ServiceEndpoint endpoint, ClientRuntime clientRuntime) =>
        clientRuntime.ClientMessageInspectors.Add(this);

    public void ApplyDispatchBehavior(ServiceEndpoint endpoint, EndpointDispatcher endpointDispatcher)
    {
    }

    public void Validate(ServiceEndpoint endpoint)
    {
    }

    public object? BeforeSendRequest(ref Message request, System.ServiceModel.IClientChannel channel)
    {
        request = Copy(request, "request");
        return null;
    }

    public void AfterReceiveReply(ref Message reply, object? correlationState) => reply = Copy(reply, "response");

    private Message Copy(Message message, string direction)
    {
        var buffer = message.CreateBufferedCopy(EFacturaClient.MaxMessageBytes);
        record(direction, buffer.CreateMessage().ToString());
        return buffer.CreateMessage();
    }
}
