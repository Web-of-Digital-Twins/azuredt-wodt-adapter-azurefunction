using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Messaging;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Azure.DigitalTwins.Core;
using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.Json;
using Azure;
using System.Collections.Generic;

namespace Unibo.WoDT
{
    public static class ADTShadowingExtension
    {
        /// <summary> Azure Digital Twin's relationships create event type. </summary>
        private const string relationshipCreateEventType = "Microsoft.DigitalTwins.Relationship.Create";
        /// <summary> Azure Digital Twin's relationships delete event type. </summary>
        private const string relationshipDeleteEventType = "Microsoft.DigitalTwins.Relationship.Delete";
        /// <summary> Azure Digital Twin's create Digital Twin event type. </summary>
        private const string createDigitalTwinEventType = "Microsoft.DigitalTwins.Twin.Create";
        /// <summary> Azure Digital Twin's delete Digital Twin event type. </summary>
        private const string deleteDigitalTwinEventType = "Microsoft.DigitalTwins.Twin.Delete";
        /// <summary> Azure Digital Twin's update Digital Twin event type. </summary>
        private const string updateDigitalTwinEventType = "Microsoft.DigitalTwins.Twin.Update";
        /// <summary> External DT model. </summary>
        private const string externalDTModel = "dtmi:io:github:wodt:ExternalDT;1";

        /// <summary> The Azure Digital Twins client. </summary>
        private static readonly DigitalTwinsClient digitalTwinsClient = new(
                new Uri(Environment.GetEnvironmentVariable("ADT_SERVICE_URL") ?? 
                    throw new ArgumentException("Pass the ADT_SERVICE_URL environment variable to the Azure Function")),
                new DefaultAzureCredential(),
                new DigitalTwinsClientOptions{ Transport = new HttpClientTransport(new HttpClient()) });

         /// <summary>A HTTP trigger function. It is used by client to be able to connect to SignalR Service.
        /// It uses the SignalRConnectionInfo input binding to generate and return valid connection information.</summary>
        /// <param name="req">the trigger of the function. Client perform a post request on this function in order to obtain the token.</param>
        /// <param name="connectionInfo">the connection information returned to the client.</param>
        [FunctionName("negotiate")]
        public static SignalRConnectionInfo GetSignalRInfo(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequest req,
            [SignalRConnectionInfo(HubName = "dteventendpointhub")] SignalRConnectionInfo connectionInfo)
        {
            return connectionInfo;
        }

        /// <summary>This Azure function handle the events from the Event Grid topic 
        /// to which it is subscribed in order to receive Azure Digital Twins events.
        /// Events are then mapped in a complete snapshot of the interested DT and sent via SignalR.
        /// <param name="eventGridEvent">the trigger of the function. It is an event from the Event Grid. The specification used is CloudEvent v1.</param>
        /// <param name="signalRConnection">the output binding to the SignalR connection used to send the updated DTs snapshots.</param>
        [FunctionName("shadower")]
        public static Task Run(
            [EventGridTrigger] CloudEvent eventGridEvent,
            [SignalR(HubName = "dteventendpointhub", ConnectionStringSetting = "AzureSignalRConnectionString")] IAsyncCollector<SignalRMessage> signalRConnection,
            ILogger log)
        {
            // Log received event
            log.LogInformation($"Received event: {eventGridEvent.Data}");

            string? dtModel = GetDigitalTwinModelFromEvent(eventGridEvent);
            // Exit if it is NOT an event of interest
            if (dtModel is null || dtModel.Equals(externalDTModel)) 
            {
                log.LogInformation("External DT update or impossible to obtain model");
                return Task.CompletedTask;
            }

            // Handle the event and create an event payload to send along the system
            JsonObject? eventToClients = HandleEvent(eventGridEvent, log);
            // Check if event CANNOT be handled
            if (eventToClients is null) {
                log.LogInformation("Impossible to map event");
                return Task.CompletedTask;
            }

            // Log event sent via Signal R
            log.LogInformation($"New event:\n {eventToClients}");

            // Send the event via SignalR
            return signalRConnection.AddAsync(new SignalRMessage {
                    Target = "digitalTwinUpdate",
                    Arguments = [eventToClients.ToJsonString()]
            });
        }

        private static string? GetDigitalTwinModelFromEvent(CloudEvent receivedEvent) {
            return receivedEvent.Type switch
            {
                createDigitalTwinEventType or deleteDigitalTwinEventType =>
                    JsonSerializer.Deserialize<JsonObject>(receivedEvent.Data?.ToString() ?? "")
                        ?.AsObject()["data"]
                        ?.AsObject()["$metadata"]
                        ?.AsObject()["$model"]
                        ?.ToString(),
                updateDigitalTwinEventType => 
                    JsonSerializer.Deserialize<JsonObject>(receivedEvent.Data?.ToString() ?? "")
                        ?.AsObject()["data"]
                        ?.AsObject()["modelId"]
                        ?.ToString(),
                relationshipCreateEventType or relationshipDeleteEventType =>
                    ((BasicDigitalTwin) digitalTwinsClient.GetDigitalTwin<BasicDigitalTwin>(GetDigitalTwinIdFromEvent(receivedEvent)))
                        .Metadata
                        .ModelId,
                _ => null,
            };
        }

        private static string? GetDigitalTwinIdFromEvent(CloudEvent receivedEvent) {
            return receivedEvent.Type switch
            {
                createDigitalTwinEventType or deleteDigitalTwinEventType or updateDigitalTwinEventType => receivedEvent.Subject,
                relationshipCreateEventType or relationshipDeleteEventType => 
                    JsonSerializer.Deserialize<JsonObject>(receivedEvent?.Data?.ToString() ?? "")
                        ?.AsObject()["data"]
                        ?.AsObject()["$sourceId"]
                        ?.ToString(),
                _ => null,
            };
        }

        private static JsonObject? HandleEvent(CloudEvent receivedEvent, ILogger log) {
                string? digitalTwinId = GetDigitalTwinIdFromEvent(receivedEvent);
                if (digitalTwinId != null) {
                    JsonObject returnedEvent = new()
                    {
                        // Add metadata to the event object
                        { "dtId",  digitalTwinId},
                        { "eventType", MapEventType(receivedEvent.Type) },
                        { "eventDateTime", receivedEvent.Time }
                    };

                    // If the event is a DT creation, an update about properties or relationships, then obtain the complete current status
                    if (receivedEvent.Type.Equals(createDigitalTwinEventType)
                        || receivedEvent.Type.Equals(updateDigitalTwinEventType)
                        || receivedEvent.Type.Equals(relationshipCreateEventType)
                        || receivedEvent.Type.Equals(relationshipDeleteEventType)) {
                        // Get and add current status
                        JsonObject digitalTwin = digitalTwinsClient.GetDigitalTwin<JsonObject>(digitalTwinId);
                        new List<string> { "$dtId", "$etag", "$metadata"}.ForEach((element) => digitalTwin.Remove(element));
                        returnedEvent["properties"] = digitalTwin;

                        // Get and add the outgoing relationships of the digital twin
                        JsonArray relationshipsArray = [];
                        returnedEvent["relationships"] = relationshipsArray;
                        Pageable<JsonObject> relationships = digitalTwinsClient.GetRelationships<JsonObject>(digitalTwinId);
                        foreach(JsonObject relationship in relationships) {
                            // Obtain target digital twin id
                            string? targetDigitalTwinID = relationship["$targetId"]?.ToString();
                            if (targetDigitalTwinID != null) {
                                new List<string> { "$relationshipId", "$etag"}.ForEach((element) => relationship.Remove(element));
                                // Obtain the target DT to understand if its an internal or an external DT -- based on its model
                                BasicDigitalTwin targetDigitalTwin = digitalTwinsClient.GetDigitalTwin<BasicDigitalTwin>(targetDigitalTwinID);
                                bool isARelationshipToExternal = targetDigitalTwin.Metadata.ModelId.Equals(externalDTModel);
                                relationship["external"] = isARelationshipToExternal;
                                relationship["$targetId"] = isARelationshipToExternal ? targetDigitalTwin.Contents["uri"].ToString() : targetDigitalTwinID;
                                relationshipsArray.Add(relationship);
                            }
                        }
                    }
                    return returnedEvent;
                }
                return null;
        }

        private static string MapEventType(string cloudEventType) {
            return cloudEventType switch
            {
                createDigitalTwinEventType => "CREATE",
                deleteDigitalTwinEventType => "DELETE",
                _ => "UPDATE",
            };
        }
    }
}
