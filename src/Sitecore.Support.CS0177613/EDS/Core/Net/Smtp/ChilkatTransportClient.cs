namespace Sitecore.Support.EDS.Core.Net.Smtp
{
    using System;
    using System.Globalization;
    using System.Threading.Tasks;
    using Chilkat;
    using Sitecore.Diagnostics;
    using Sitecore.EDS.Core.Diagnostics;
    using Sitecore.EDS.Core.Exceptions;
    using Sitecore.EDS.Core.Net;
    using Sitecore.EDS.Core.Net.Smtp;
    using Sitecore.ExM.Framework.Diagnostics;
    using LoggerFactory = Sitecore.EDS.Core.Diagnostics.LoggerFactory;
    using Task = System.Threading.Tasks.Task;

    public class ChilkatTransportClient : ChilkatClientBase<ISmtpSettings>, ITransportClient
    {

        private const string DefaultUserAndPassword = "Anonymous";

        private const int MaxMessagesPerConnection = 10000;

        private readonly ILogger logger;

        public ChilkatTransportClient([NotNull] ISmtpSettings settings)
            : this(settings, LoggerFactory.Instance)
        {
        }

        public ChilkatTransportClient([NotNull] ISmtpSettings settings, [NotNull] ILoggerFactory loggerFactory)
            : this(settings, loggerFactory.Logger)
        {
            Assert.ArgumentNotNull(settings, "settings");
            Assert.ArgumentNotNull(loggerFactory, "loggerFactory");
        }

        public ChilkatTransportClient([NotNull] ISmtpSettings settings, [NotNull] ILogger logger)
            : base(settings)
        {
            Assert.ArgumentNotNull(settings, "settings");
            Assert.ArgumentNotNull(logger, "logger");

            this.logger = logger;
        }

        public bool IsInUse { get; set; }

        public int MessagesSent { get; set; }

        public DateTime? LastUsed { get; set; }

        public bool MarkedForRemoval { get; set; }

        public Task SendAsync(Email message)
        {
            Assert.ArgumentNotNull(message, "message");

            try
            {
                var sendResult = this.TrySend(message);
                if (!sendResult)
                {
                    logger.LogError(string.Format(CultureInfo.InvariantCulture, "SendEmailError: {0}, ", this.LastErrorText));
                    throw this.GetException(this.SmtpFailReason);
                }

                MessagesSent++;
            }
            finally
            {
                this.Release();
            }

            return Task.FromResult(0);
        }

        public Task<bool> ValidateSmtpConnection()
        {
            var verify = this.VerifySmtpLogin();
            if (!verify)
            {
                this.logger.LogError("ValidateSmtpConnection: " + this.LastErrorText);
                this.MarkAsFaulted();
            }

            this.Release();

            return Task.FromResult(verify);
        }
        public void Release()
        {
            this.IsInUse = false;
            this.LastUsed = DateTime.Now;

            if (MessagesSent > MaxMessagesPerConnection)
            {
                this.MarkedForRemoval = true;
            }
        }

        public void MarkAsFaulted()
        {
            this.CloseConnection();
            this.MarkedForRemoval = true;
        }

        protected override void InitializeSettings(ISmtpSettings settings)
        {
            Assert.ArgumentNotNullOrEmpty(settings.Server, "settings.Server");
            Assert.ArgumentCondition(settings.Port > 0, "settings.Port", Sitecore.EDS.Core.Constants.Texts.MissingPort);

            this.SmtpHost = settings.Server;
            this.StartTLS = settings.StartTls;
            this.SmtpPort = settings.Port;

            this.SmtpAuthMethod = settings.AuthenticationMethod.StringValue();

            var user = settings.UserName;
            var pass = settings.Password;

            if (settings.AuthenticationMethod == AuthenticationMethod.None)
            {
                user = string.IsNullOrEmpty(settings.UserName) ? DefaultUserAndPassword : settings.UserName;
                pass = string.IsNullOrEmpty(settings.Password) ? DefaultUserAndPassword : settings.Password;
            }

            this.SmtpUsername = user;
            this.SmtpPassword = pass;
            this.SmtpLoginDomain = settings.LoginDomain;
        }

        protected virtual bool TrySend(Email message)
        {
            return this.SendEmail(message);
        }

        protected Exception GetException(string failReason)
        {
            switch (failReason)
            {
                /* Transient
                ConnectionLost     // The connection to the SMTP server was lost at some point during the method call.
                Timeout            // A timeout occurred when reading or writing the socket connection.
                GreetingError      // The SMTP server immediately responded with an error status in the intial greeting.
                */
                case "ConnectionLost":
                case "Timeout":
                case "GreetingError":
                    return new ConnectionLostException(Sitecore.EDS.Core.Constants.Texts.ConnectionLost);

                /* Fatal
                Failed             // A general failure not covered by any of the other possible keywords.
                NoSmtpHostname     // The application failed to provide an SMTP hostname or IP address.
                ConnectFailed      // Unable to establish a TCP or TLS connection to the SMTP server.
                InternalFailure    // An internal failure that should be reported to Chilkat support.
                NotUnlocked        // The UnlockComponent method was not previously called on at least one instance of the mailman object.
                Aborted            // The application aborted the method.
                StartTlsFailed     // Failed to convert the TCP connection to TLS via STARTTLS.
                */
                case "StartTlsFailed":
                case "Failed":
                case "NoSmtpHostname":
                case "ConnectFailed":
                case "InternalFailure":
                case "NotUnlocked":
                case "Aborted":
                    this.MarkAsFaulted();
                    return new TransportException(Sitecore.EDS.Core.Constants.Texts.InternalFailure);

                /* Authentication fatal
                NoCredentials      // The application did not provide the required credentials, such as username or password.
                AuthFailure        // The login (authentication) failed.
                */
                case "NoCredentials":
                case "AuthFailure":
                    this.MarkAsFaulted();
                    return new AuthenticationFailedException(Sitecore.EDS.Core.Constants.Texts.AuthenticationFailed);
            }

            /* Message related
            NoValidRecipients  // The SMTP server rejected all receipients.
            NoRecipients       // The app failed to provide any recipients (TO, CC, or BCC).
            NoFrom             // The failed to provide a FROM address.
            SomeBadRecipients  // The AllOrNone property is true, and some recipients were rejected by the SMTP server.
            FromFailure        // The SMTP replied with an error in response to the "MAIL FROM" command.
            RenderFailed       // A failure occurred when rendering the email. (Rendering the email for sending includes tasks such as signing or encrypting.)
            DataFailure        // The SMTP replied with an error in response to the "DATA" command.
            */
            return new InvalidMessageException(Sitecore.EDS.Core.Constants.Texts.IncorrectData + "Chilkat FailReason: " + failReason);
        }

        public void CloseConnection()
        {
            if (this.IsSmtpConnected)
            {
                this.CloseSmtpConnection();
            }
        }
    }
}
