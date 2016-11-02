﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.EventHubs.Amqp.Management
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Threading.Tasks;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Encoding;
    using Microsoft.Azure.Amqp.Framing;

    class AmqpServiceClient : ClientEntity
    {
        static readonly string[] RequiredClaims = { ClaimConstants.Manage, ClaimConstants.Listen };

        readonly AmqpEventHubClient eventHubClient;
        readonly FaultTolerantAmqpObject<RequestResponseAmqpLink> link;
        readonly ActiveClientLinkManager clientLinkManager;

        public AmqpServiceClient(AmqpEventHubClient eventHubClient, string address)
            : base("AmqpServiceClient-" + StringUtility.GetRandomString())
        {
            this.eventHubClient = eventHubClient;
            this.Address = address;
            this.link = new FaultTolerantAmqpObject<RequestResponseAmqpLink>(t => this.OpenLinkAsync(t), rrlink => rrlink.CloseAsync(TimeSpan.FromSeconds(10)));
            this.clientLinkManager = new ActiveClientLinkManager(this.eventHubClient);
        }

        AmqpMessage CreateGetRuntimeInformationRequest()
        {
            AmqpMessage getRuntimeInfoRequest = AmqpMessage.Create();
            getRuntimeInfoRequest.ApplicationProperties = new ApplicationProperties();
            getRuntimeInfoRequest.ApplicationProperties.Map[AmqpClientConstants.EntityNameKey] = this.eventHubClient.EventHubName;
            getRuntimeInfoRequest.ApplicationProperties.Map[AmqpClientConstants.ManagementOperationKey] = AmqpClientConstants.ReadOperationValue;
            getRuntimeInfoRequest.ApplicationProperties.Map[AmqpClientConstants.ManagementEntityTypeKey] = AmqpClientConstants.ManagementEventHubEntityTypeValue;

            return getRuntimeInfoRequest;
        }

        public async Task<EventHubRuntimeInformation> GetRuntimeInformationAsync(string token)
        {
            RequestResponseAmqpLink requestLink = await this.link.GetOrCreateAsync(TimeSpan.FromMinutes(1)).ConfigureAwait(false);

            // Create request and attach token.
            var request = this.CreateGetRuntimeInformationRequest();
            request.ApplicationProperties.Map[AmqpClientConstants.ManagementSecurityTokenKey] = token;

            var response = await requestLink.RequestAsync(request, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            int statusCode = (int)response.ApplicationProperties.Map[AmqpClientConstants.ResponseStatusCode];
            string statusDescription = (string)response.ApplicationProperties.Map[AmqpClientConstants.ResponseStatusDescription];
            if (statusCode != (int)AmqpResponseStatusCode.Accepted && statusCode != (int)AmqpResponseStatusCode.OK)
            {
                AmqpSymbol errorCondition = AmqpExceptionHelper.GetResponseErrorCondition(response, (AmqpResponseStatusCode)statusCode);
                Error error = new Error { Condition = errorCondition, Description = statusDescription };
                throw AmqpExceptionHelper.ToMessagingContract(error);
            }

            AmqpMap infoMap = null;
            if (response.ValueBody != null)
            {
                infoMap = response.ValueBody.Value as AmqpMap;
            }

            if (infoMap == null)
            {
                throw new InvalidOperationException($"Return type mismatch in GetRuntimeInformationAsync. Response returned NULL or response isn't AmqpMap.");
            }

            return new EventHubRuntimeInformation()
            {
                Type = (string)infoMap[new MapKey("type")],
                Path = (string)infoMap[new MapKey("name")],
                CreatedAt = (DateTime)infoMap[new MapKey("created_at")],
                PartitionCount = (int)infoMap[new MapKey("partition_count")],
                PartitionIds = (string[])infoMap[new MapKey("partition_ids")],
            };
        }

        public string Address { get; }

        public override Task CloseAsync()
        {
            return this.link.CloseAsync();
        }

        internal void OnAbort()
        {
            RequestResponseAmqpLink innerLink;
            if (this.link.TryGetOpenedObject(out innerLink))
            {
                innerLink?.Abort();
            }
        }

        async Task<RequestResponseAmqpLink> OpenLinkAsync(TimeSpan timeout)
        {
            ActiveClientRequestResponseLink activeClientLink = await this.eventHubClient.OpenRequestResponseLinkAsync(
                "svc", this.Address, null, AmqpServiceClient.RequiredClaims, timeout);
            this.clientLinkManager.SetActiveLink(activeClientLink);
            return activeClientLink.Link;
        }
    }
}
