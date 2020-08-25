﻿namespace NServiceBus.Transport.AzureServiceBus
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using Azure.Core;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Management;
    using Microsoft.Azure.ServiceBus.Primitives;

    class QueueCreator : ICreateQueues
    {
        const int maxNameLength = 50;

        readonly string mainInputQueueName;
        readonly string topicName;
        readonly ServiceBusConnectionStringBuilder connectionStringBuilder;
        readonly ITokenProvider tokenProvider;
        readonly NamespacePermissions namespacePermissions;
        readonly int maxSizeInMB;
        readonly bool enablePartitioning;
        readonly Func<string, string> subscriptionShortener;

        public QueueCreator(string mainInputQueueName, string topicName, ServiceBusConnectionStringBuilder connectionStringBuilder, TokenCredential tokenProvider, NamespacePermissions namespacePermissions, int maxSizeInMB, bool enablePartitioning, Func<string, string> subscriptionShortener)
        {
            this.mainInputQueueName = mainInputQueueName;
            this.topicName = topicName;
            this.connectionStringBuilder = connectionStringBuilder;
            this.tokenProvider = tokenProvider;
            this.namespacePermissions = namespacePermissions;
            this.maxSizeInMB = maxSizeInMB;
            this.enablePartitioning = enablePartitioning;
            this.subscriptionShortener = subscriptionShortener;
        }

        public async Task CreateQueueIfNecessary(QueueBindings queueBindings, string identity)
        {
            var result = await namespacePermissions.CanManage().ConfigureAwait(false);

            if (!result.Succeeded)
            {
                throw new Exception(result.ErrorMessage);
            }

            var client = new ManagementClient(connectionStringBuilder, tokenProvider);

            try
            {
                var topic = new TopicDescription(topicName)
                {
                    EnableBatchedOperations = true,
                    EnablePartitioning = enablePartitioning,
                    MaxSizeInMB = maxSizeInMB
                };

                try
                {
                    await client.CreateTopicAsync(topic).ConfigureAwait(false);
                }
                catch (MessagingEntityAlreadyExistsException)
                {
                }
                // TODO: refactor when https://github.com/Azure/azure-service-bus-dotnet/issues/525 is fixed
                catch (ServiceBusException sbe) when (sbe.Message.Contains("SubCode=40901.")) // An operation is in progress.
                {
                }

                foreach (var address in queueBindings.ReceivingAddresses.Concat(queueBindings.SendingAddresses))
                {
                    var queue = new QueueDescription(address)
                    {
                        EnableBatchedOperations = true,
                        LockDuration = TimeSpan.FromMinutes(5),
                        MaxDeliveryCount = int.MaxValue,
                        MaxSizeInMB = maxSizeInMB,
                        EnablePartitioning = enablePartitioning
                    };

                    try
                    {
                        await client.CreateQueueAsync(queue).ConfigureAwait(false);
                    }
                    catch (MessagingEntityAlreadyExistsException)
                    {
                    }
                    // TODO: refactor when https://github.com/Azure/azure-service-bus-dotnet/issues/525 is fixed
                    catch (ServiceBusException sbe) when (sbe.Message.Contains("SubCode=40901.")) // An operation is in progress.
                    {
                    }
                }

                var subscriptionName = mainInputQueueName.Length > maxNameLength ? subscriptionShortener(mainInputQueueName) : mainInputQueueName;
                var subscription = new SubscriptionDescription(topicName, subscriptionName)
                {
                    LockDuration = TimeSpan.FromMinutes(5),
                    ForwardTo = mainInputQueueName,
                    EnableDeadLetteringOnFilterEvaluationExceptions = false,
                    MaxDeliveryCount = int.MaxValue,
                    EnableBatchedOperations = true,
                    UserMetadata = mainInputQueueName
                };

                try
                {
                    await client.CreateSubscriptionAsync(subscription, new RuleDescription("$default", new FalseFilter())).ConfigureAwait(false);
                }
                catch (MessagingEntityAlreadyExistsException)
                {
                }
                // TODO: refactor when https://github.com/Azure/azure-service-bus-dotnet/issues/525 is fixed
                catch (ServiceBusException sbe) when (sbe.Message.Contains("SubCode=40901.")) // An operation is in progress.
                {
                }
            }
            finally
            {
                await client.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}