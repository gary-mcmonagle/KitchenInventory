using Azure;
using Azure.Communication.Email;
using CupboardTracker.Application.Notifications;
using AppEmailContent = CupboardTracker.Application.Notifications.EmailContent;

namespace CupboardTracker.Infrastructure.Email;

/// <summary>Sends through Azure Communication Services Email with the app's managed identity (no keys). The SDK
/// retries transient failures; a final failure throws, and the caller logs it.</summary>
public sealed class AcsEmailNotifier(EmailClient client, EmailOptions options) : INotifier
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        var message = ToMessage(notification, options);
        // Started: don't poll for delivery; ACS accepted it.
        await client.SendAsync(WaitUntil.Started, message, cancellationToken);
    }

    internal static EmailMessage ToMessage(Notification notification, EmailOptions options)
    {
        AppEmailContent content = notification.Content;
        return new EmailMessage(
            senderAddress: options.Sender,
            recipientAddress: options.RecipientFor(notification.Kind),
            content: new Azure.Communication.Email.EmailContent(content.Subject)
            {
                Html = content.Html,
                PlainText = content.PlainText,
            });
    }
}
