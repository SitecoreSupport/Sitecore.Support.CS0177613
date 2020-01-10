namespace Sitecore.Support.EDS.Core.Net.Smtp
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using Sitecore.Diagnostics;
    using Sitecore.EDS.Core.Exceptions;
    using Sitecore.EDS.Core.Net.Smtp;
    using Sitecore.ExM.Framework.Diagnostics;

    public class ChilkatConnectionPool : IChilkatConnectionPool
    {

        private readonly ConnectionPoolSettings poolSettings;

        public ISmtpSettings SmtpSettings { get; set; }

        private readonly List<ITransportClient> connections;

        private readonly object connectionLock = new object();

        private ILogger _logger;

        public ChilkatConnectionPool(ConnectionPoolSettings settings, ISmtpSettings smtpSettings, ILogger logger)
        {
            Assert.ArgumentNotNull(settings, "settings");
            Assert.ArgumentNotNull(smtpSettings, "smtpSettings");
            Assert.ArgumentNotNull(logger, "logger");

            this.poolSettings = settings;
            SmtpSettings = smtpSettings;
            _logger = logger;
            this.connections = new List<ITransportClient>();
        }

        public int Cleanup()
        {
            var markedForRemoval = this.connections.Where(this.IsRemovable()).ToArray();
            foreach (ITransportClient removalableConnection in markedForRemoval)
            {
                this.connections.Remove(removalableConnection);
                if (removalableConnection != null)
                {
                    removalableConnection.CloseConnection();
                    removalableConnection.Dispose();
                }
            }

            return markedForRemoval.Count();
        }

        public async Task<ITransportClient> GetConnectionAsync()
        {
            var waitTime = new Stopwatch();
            waitTime.Start();

            for (var i = 0; i < this.poolSettings.MaxConnectionRetries; i++)
            {
                var connection = this.TryGetConnection();
                if (connection != null)
                {
                    return connection;
                }

                if (waitTime.Elapsed > this.poolSettings.MaxConnectionWaitTime)
                {
                    throw new ConnectionTimeoutException(Sitecore.EDS.Core.Constants.Texts.UnableToObtainConnection);
                }

                await Task.Delay(this.poolSettings.DelayBetweenConnectionRetries);
            }

            throw new ConnectionTimeoutException(Sitecore.EDS.Core.Constants.Texts.UnableToObtainConnectionOnRetry);
        }

        internal ITransportClient CreateConnection()
        {
            if (SmtpSettings != null)
            {
                return new Sitecore.Support.EDS.Core.Net.Smtp.ChilkatTransportClient(SmtpSettings);
            }

            _logger.LogError("SMTP settings have not been initialized. Failed to create connection.");
            throw new TransportException(Sitecore.EDS.Core.Constants.Texts.MissingSmtpSettings);
        }

        private ITransportClient TryGetConnection()
        {
            lock (this.connectionLock)
            {
                var connectionPoolEntry = this.connections.FirstOrDefault(this.IsAvailable);
                if (connectionPoolEntry != null)
                {
                    connectionPoolEntry.IsInUse = true;
                    return connectionPoolEntry;
                }

                if (this.connections.Count(this.IsHealthy) >= this.poolSettings.MaxPoolSize)
                {
                    return null;
                }

                connectionPoolEntry = this.CreateConnection();
                if (connectionPoolEntry == null)
                {
                    return null;
                }
                connectionPoolEntry.IsInUse = true;

                this.connections.Add(connectionPoolEntry);
                return connectionPoolEntry;
            }
        }

        private bool IsHealthy(ITransportClient connection)
        {
            return !connection.MarkedForRemoval;
        }

        private bool IsAvailable(ITransportClient connection)
        {
            return !connection.IsInUse && !connection.MarkedForRemoval;
        }

        private Func<ITransportClient, bool> IsRemovable()
        {
            return conn => conn.MarkedForRemoval || (!conn.IsInUse && DateTime.Now - conn.LastUsed > this.poolSettings.MaxConnectionIdleTime);
        }
    }
}
