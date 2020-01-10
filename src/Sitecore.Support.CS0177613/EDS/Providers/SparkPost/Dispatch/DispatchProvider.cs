namespace Sitecore.Support.EDS.Providers.SparkPost.Dispatch
{
    using System;
    using System.Collections.Generic;
    using Sitecore.EDS.Core.Dispatch;
    using Sitecore.EDS.Core.Reporting;
    using Sitecore.EDS.Providers.SparkPost.Configuration;
    using Sitecore.EDS.Providers.SparkPost.Dispatch;
    using Sitecore.EDS.Providers.SparkPost.Services;
    using Newtonsoft.Json.Linq;
    using Sitecore.StringExtensions;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Sitecore.Diagnostics;
    using Sitecore.EDS.Core.Exceptions;
    using Sitecore.EDS.Core.Net.Smtp;

    public class DispatchProvider : Sitecore.EDS.Providers.SparkPost.Dispatch.DispatchProvider
    {
        private readonly ConnectionPoolManager _connectionPoolManager;
        private readonly IConfigurationStore _configurationStore;
        private readonly string _returnPath;
        private readonly int _maxTries;
        private readonly int _delay;


        public DispatchProvider([NotNull] ConnectionPoolManager connectionPoolManager, [NotNull] IEnvironmentId environmentIdentifier, [NotNull] IConfigurationStore configurationStore, [NotNull] string returnPath, [NotNull]  string maxTries = "3", [NotNull]  string delay = "1000")
            : base(connectionPoolManager, environmentIdentifier, configurationStore, returnPath)
        {
            Assert.ArgumentNotNull(connectionPoolManager, "connectionPoolManager");

            _connectionPoolManager = connectionPoolManager;
            _configurationStore = configurationStore;
            _returnPath = returnPath;
            _maxTries = Int32.Parse(maxTries);
            _delay = Int32.Parse(delay);
        }

        public override async Task<bool> ValidateDispatchAsync()
        {
            for (var i = 0; i < _maxTries; i++)
            {

                var client = await _connectionPoolManager.GetSmtpConnectionAsync();

                if (!client.ValidateSmtpConnection().Result)
                {

                    if (i == _maxTries - 1)
                    {
                        return false;
                    }
                    Thread.Sleep(_delay);
                }
                else
                {
                    return true;
                }
            }
            return false;
        }

        protected override async Task<DispatchResult> SendEmailAsync(EmailMessage message)
        {
            for (var i = 0; i < _maxTries; i++)
            {
                message.ReturnPath = _returnPath;
                try
                {
                    var stopwatch = Stopwatch.StartNew();

                    var chilkatMessageTransport = new ChilkatMessageTransport(message);

                    stopwatch.Stop();
                    string parseMessageElapsed = stopwatch.ElapsedMilliseconds.ToString();
                    stopwatch.Restart();

                    var client = await _connectionPoolManager.GetSmtpConnectionAsync();

                    stopwatch.Stop();
                                                         
                    var dispatchResult = await chilkatMessageTransport.SendAsync(client);
                    dispatchResult.Statistics.Add("ParseMessage", parseMessageElapsed);
                    dispatchResult.Statistics.Add("GetConnection", stopwatch.ElapsedMilliseconds.ToString());

                    return dispatchResult;
                }

                catch (TransportException)
                {

                    if (i == _maxTries - 1)
                    {
                        throw;
                    }
                    Thread.Sleep(_delay);
                }
            }
            return null;
        }

        protected override void SetMessageHeaders(EmailMessage message)
        {
            SparkPostClientCredentials credentials = this._configurationStore.GetCredentials(false);

            JObject jObject = JObject.FromObject(new
            {
                options = new Dictionary<string, object>
    {
      {
        "open_tracking",
        (object)credentials.OpenTracking
      },
      {
        "click_tracking",
        (object)credentials.ClickTracking
      },
      {
        "ip_pool",
        (object)(credentials.IpPool ?? "sp_shared")
      }
    },
                metadata = new Dictionary<string, string>
    {
          {
        "contact_id",
        message.ContactIdentifier
      },
      {
        "contact_identifier",
        message.ContactIdentifier
      },
      {
        "message_id",
        message.MessageId
      },
      {
        "instance_id",
        message.MessageId
      },
      {
        "campaign_id",
        message.CampaignId
      },
      {
        "target_language",
        message.TargetLanguage
      },
      {
        "test_value_index",
        message.TestValueIndex.HasValue ? message.TestValueIndex.Value.ToString() : string.Empty
      },
      {
        "email_address_history_entry_id",
        message.EmailAddressHistoryEntryId.ToString()
      }
    }
            });

            message.Headers.Set("X-MSYS-API", jObject.ToString());

            if (!message.ReplyTo.IsNullOrEmpty())
            {
                message.Headers.Set("Reply-To", message.ReplyTo);
            }
        }
    }
}