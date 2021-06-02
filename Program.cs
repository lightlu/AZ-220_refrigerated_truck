using System;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using AzureMapsToolkit;
using AzureMapsToolkit.Common;
using uPLibrary.Networking.M2Mqtt;  //for MQTT subscribe
using uPLibrary.Networking.M2Mqtt.Messages;  //for MQTT subscribe

namespace refrigerated_truck
{
    class Program
    {
        enum StateEnum
        {
            ready,
            enroute,
            delivering,
            returning,
            loading,
            dumping
        };
        enum ContentsEnum
        {
            full,
            melting,
            empty
        }
        enum FanEnum
        {
            on,
            off,
            failed
        }

        // Azure maps service globals.
        static AzureMapsServices azureMapsServices;

        // Telemetry globals.
        const int intervalInMilliseconds = 5000;        // Time interval required by wait function.

        // Refrigerated truck globals.
        static int truckNum = 1;
        static string truckIdentification = "Truck number " + truckNum;

        const double deliverTime = 600;                 // Time to complete delivery, in seconds.
        const double loadingTime = 800;                 // Time to load contents.
        const double dumpingTime = 400;                 // Time to dump melted contents.
        const double tooWarmThreshold = 2;              // Degrees C that is too warm for contents.
        const double tooWarmtooLong = 60;               // Time in seconds for contents to start melting if temps are above threshold.


        static double timeOnCurrentTask = 0;            // Time on current task in seconds.
        static double interval = 60;                    // Simulated time interval in seconds.
        static double tooWarmPeriod = 0;                // Time that contents are too warm in seconds.
        static double tempContents = -2;                // Current temp of contents in degrees C.
        static double baseLat = 47.644702;              // Base position latitude.
        static double baseLon = -122.130137;            // Base position longitude.
        static double currentLat;                       // Current position latitude.
        static double currentLon;                       // Current position longitude.
        static double destinationLat;                   // Destination position latitude.
        static double destinationLon;                   // Destination position longitude.

        static FanEnum fan = FanEnum.on;                // Cooling fan state.
        static ContentsEnum contents = ContentsEnum.full;    // Truck contents state.
        static StateEnum state = StateEnum.ready;       // Truck is full and ready to go!
        static double optimalTemperature = -5;         // Setting - can be changed by the operator from IoT Central.

        const string noEvent = "none";
        static string eventText = noEvent;              // Event text sent to IoT Central.

        static double[,] customer = new double[,]
        {
            // Lat/lon position of customers.
            // Gasworks Park
            {47.645892, -122.336954},

            // Golden Gardens Park
            {47.688741, -122.402965},

            // Seward Park
            {47.551093, -122.249266},

            // Lake Sammamish Park
            {47.555698, -122.065996},

            // Marymoor Park
            {47.663747, -122.120879},

            // Meadowdale Beach Park
            {47.857295, -122.316355},

            // Lincoln Park
            {47.530250, -122.393055},

            // Gene Coulon Park
            {47.503266, -122.200194},

            // Luther Bank Park
            {47.591094, -122.226833},

            // Pioneer Park
            {47.544120, -122.221673 }
        };

	// Gene Coulon Park
	static double[,] routeCustomer7 = new double[,]
	{
		{47.639125559793534, -122.1357198427834},
		{47.63666517962163, -122.14304},
		{47.629693778092395, -122.15396510832448},
		{47.62995688097588, -122.16986238927277},
		{47.63204395080198, -122.18538505027742},
		{47.627275040434746, -122.18887739060114},
		{47.616484826042985, -122.1889637743807},
		{47.60593955580794, -122.18666113967859},
		{47.59597402791651, -122.18160013281326},
		{47.58552618289185, -122.17841024241707},
		{47.57535496379873, -122.17544442405745},
		{47.56630478119958, -122.18315230622487},
		{47.55709680293006, -122.19015361611747}
	};

        static double[,] path;                          // Lat/lon steps for the route.
        static double[] timeOnPath;                     // Time in seconds for each section of the route.
        static int truckOnSection;                      // The current path section the truck is on.
        static double truckSectionsCompletedTime;       // The time the truck has spent on previous completed sections.
        static Random rand;

        // IoT Central global variables.
        static DeviceClient s_deviceClient;
        static CancellationTokenSource cts;
        static string GlobalDeviceEndpoint = "global.azure-devices-provisioning.net";
        static TwinCollection reportedProperties = new TwinCollection();

        // User IDs.
        static string ScopeID = "0ne002B7B53";
        static string DeviceID = "RefrigeratedTruck1";
        static string PrimaryKey = "EqnsRtACd6SthkkrGkSQlYkF6RLQLZarYCptmCZ/TG0=";
        static string AzureMapsKey = "k7n6jLNqpwOyZuvXoD5pHgE8zUXGtIcI5ILNY1lJNzU";

        static double Degrees2Radians(double deg)
        {
            return deg * Math.PI / 180;
        }

        // Returns the distance in meters between two locations on Earth.
        static double DistanceInMeters(double lat1, double lon1, double lat2, double lon2)
        {
            var dlon = Degrees2Radians(lon2 - lon1);
            var dlat = Degrees2Radians(lat2 - lat1);

            var a = (Math.Sin(dlat / 2) * Math.Sin(dlat / 2)) + Math.Cos(Degrees2Radians(lat1)) * Math.Cos(Degrees2Radians(lat2)) * (Math.Sin(dlon / 2) * Math.Sin(dlon / 2));
            var angle = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            var meters = angle * 6371000;
            return meters;
        }

        static bool Arrived()
        {
            // If the truck is within 10 meters of the destination, call it good.
            if (DistanceInMeters(currentLat, currentLon, destinationLat, destinationLon) < 10)
                return true;
            return false;
        }

        static void UpdatePosition()
        {
            while ((truckSectionsCompletedTime + timeOnPath[truckOnSection] < timeOnCurrentTask) && (truckOnSection < timeOnPath.Length - 1))
            {
                // Truck has moved onto the next section.
                truckSectionsCompletedTime += timeOnPath[truckOnSection];
                ++truckOnSection;
            }

            // Ensure remainder is 0 to 1, as interval may take count over what is needed.
            var remainderFraction = Math.Min(1, (timeOnCurrentTask - truckSectionsCompletedTime) / timeOnPath[truckOnSection]);

            // The path should be one entry longer than the timeOnPath array.
            // Find how far along the section the truck has moved.
            currentLat = path[truckOnSection, 0] + remainderFraction * (path[truckOnSection + 1, 0] - path[truckOnSection, 0]);
            currentLon = path[truckOnSection, 1] + remainderFraction * (path[truckOnSection + 1, 1] - path[truckOnSection, 1]);
	    greenMessage($"UpdatePosition() currentLat,currentLon = {currentLat},{currentLon}");
        }
	    private static int update_position_index = 0;
	    static void UpdatePositionLocal()
		    {

			    currentLat = path[update_position_index, 0];
			    currentLon = path[update_position_index, 1];
			    update_position_index++;
			    greenMessage($"UpdatePositionLocal() {update_position_index} currentLat,currentLon = {currentLat},{currentLon}");
		    }

        static void GetRoute(StateEnum newState)
        {
            // Set the state to ready, until the new route arrives.
            state = StateEnum.ready;

            var req = new RouteRequestDirections
            {
                Query = FormattableString.Invariant($"{currentLat},{currentLon}:{destinationLat},{destinationLon}")
            };
            var directions = azureMapsServices.GetRouteDirections(req).Result;

            if (directions.Error != null || directions.Result == null)
            {
                // Handle any error.
                redMessage("Failed to find map route");
            }
            else
            {
                int nPoints = directions.Result.Routes[0].Legs[0].Points.Length;
                greenMessage($"Route found. Number of points = {nPoints}");

                // Clear the path. Add two points for the start point and destination.
                path = new double[nPoints + 2, 2];
                int c = 0;

                // Start with the current location.
                path[c, 0] = currentLat;
                path[c, 1] = currentLon;
                ++c;

                // Retrieve the route and push the points onto the array.
                for (var n = 0; n < nPoints; n++)
                {
                    var x = directions.Result.Routes[0].Legs[0].Points[n].Latitude;
                    var y = directions.Result.Routes[0].Legs[0].Points[n].Longitude;
                    path[c, 0] = x;
                    path[c, 1] = y;
                    ++c;
                }

                // Finish with the destination.
                path[c, 0] = destinationLat;
                path[c, 1] = destinationLon;

                // Store the path length and time taken, to calculate the average speed.
                var meters = directions.Result.Routes[0].Summary.LengthInMeters;
                var seconds = directions.Result.Routes[0].Summary.TravelTimeInSeconds;
                var pathSpeed = meters / seconds;

                double distanceApartInMeters;
                double timeForOneSection;

                // Clear the time on path array. The path array is 1 less than the points array.
                timeOnPath = new double[nPoints + 1];

                // Calculate how much time is required for each section of the path.
                for (var t = 0; t < nPoints + 1; t++)
                {
                    // Calculate distance between the two path points, in meters.
                    distanceApartInMeters = DistanceInMeters(path[t, 0], path[t, 1], path[t + 1, 0], path[t + 1, 1]);

                    // Calculate the time for each section of the path.
                    timeForOneSection = distanceApartInMeters / pathSpeed;
                    timeOnPath[t] = timeForOneSection;
                }
                truckOnSection = 0;
                truckSectionsCompletedTime = 0;
                timeOnCurrentTask = 0;

                // Update the state now the route has arrived. One of: enroute or returning.
                state = newState;
            }
        }

	    static void GetRouteLocal(StateEnum newState)
	    {
		    greenMessage($"newState={newState}");
		    // Set the state to ready, until the new route arrives.
		    state = StateEnum.ready;
		    int nPoints = routeCustomer7.Length / 2;
		    greenMessage($"Use Local Route routeCustomer7. Number of points = {nPoints}");

		    // Clear the path. Add two points for the start point and destination.
		    path = new double[nPoints + 2, 2];
		    int c = 0;

		    // Start with the current location.
		    path[c, 0] = currentLat;
		    path[c, 1] = currentLon;
		    greenMessage($"c={c} currentLat={currentLat} currentLon={currentLon} path[c, ]={path[c, 0]}, {path[c, 1]}");
		    ++c;

		    // Retrieve the route and push the points onto the array.
		    for (var n = 0; n < nPoints; n++)
		    {
			    var x = routeCustomer7[n, 0];
			    var y = routeCustomer7[n, 1];
			    path[c, 0] = x;
			    path[c, 1] = y;
			    greenMessage($"c={c} path[{c}, ]={path[c, 0]}, {path[c, 1]}");
			    ++c;
		    }

		    // Finish with the destination.
		    destinationLat = path[c, 0];
		    destinationLon = path[c, 1];

		    // Store the path length and time taken, to calculate the average speed.
		    var meters = DistanceInMeters(path[0, 0], path[0, 1], destinationLat, destinationLon);
		    var seconds = 100;
		    var pathSpeed = meters / seconds;

		    double distanceApartInMeters;
		    double timeForOneSection;

		    // Clear the time on path array. The path array is 1 less than the points array.
		    timeOnPath = new double[nPoints + 1];

		    // Calculate how much time is required for each section of the path.
		    for (var t = 0; t < nPoints + 1; t++)
		    {
			    // Calculate distance between the two path points, in meters.
			    distanceApartInMeters = DistanceInMeters(path[t, 0], path[t, 1], path[t + 1, 0], path[t + 1, 1]);

			    // Calculate the time for each section of the path.
			    timeForOneSection = distanceApartInMeters / pathSpeed;
			    timeOnPath[t] = timeForOneSection;
		    }
		    truckOnSection = 0;
		    truckSectionsCompletedTime = 0;
		    timeOnCurrentTask = 0;

		    // Update the state now the route has arrived. One of: enroute or returning.
		    state = newState;
        }

    static Task<MethodResponse> CmdGoToCustomer(MethodRequest methodRequest, object userContext)
    {
        try
        {
            // Pick up variables from the request payload, with the name specified in IoT Central.
            var payloadString = Encoding.UTF8.GetString(methodRequest.Data);
            int customerNumber = Int32.Parse(payloadString);

            // Check for a valid key and customer ID.
            if (customerNumber >= 0 && customerNumber < customer.Length)
            {
                switch (state)
                {
                    case StateEnum.dumping:
                    case StateEnum.loading:
                    case StateEnum.delivering:
                        eventText = "Unable to act - " + state;
                        break;

                    case StateEnum.ready:
                    case StateEnum.enroute:
                    case StateEnum.returning:
                        if (contents == ContentsEnum.empty)
                        {
                            eventText = "Unable to act - empty";
                        }
                        else
                        {
                            // Set event only when all is good.
                            eventText = "New customer: " + customerNumber.ToString();

                            destinationLat = customer[customerNumber, 0];
                            destinationLon = customer[customerNumber, 1];

                            // Find route from current position to destination, storing route.
                            GetRouteLocal(StateEnum.enroute);
                        }
                        break;
                }

                // Acknowledge the direct method call with a 200 success message.
                string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
            }
            else
            {
                eventText = $"Invalid customer: {customerNumber}";

                // Acknowledge the direct method call with a 400 error message.
                string result = "{\"result\":\"Invalid customer\"}";
                return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
            }
        }
        catch
        {
            // Acknowledge the direct method call with a 400 error message.
            string result = "{\"result\":\"Invalid call\"}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
        }
    }

    static void ReturnToBase()
    {
        destinationLat = baseLat;
        destinationLon = baseLon;

        // Find route from current position to base, storing route.
        GetRoute(StateEnum.returning);
    }
    static Task<MethodResponse> CmdRecall(MethodRequest methodRequest, object userContext)
    {
        switch (state)
        {
            case StateEnum.ready:
            case StateEnum.loading:
            case StateEnum.dumping:
                eventText = "Already at base";
                break;

            case StateEnum.returning:
                eventText = "Already returning";
                break;

            case StateEnum.delivering:
                eventText = "Unable to recall - " + state;
                break;

            case StateEnum.enroute:
                ReturnToBase();
                break;
        }

        // Acknowledge the command.
        if (eventText == noEvent)
        {
            // Acknowledge the direct method call with a 200 success message.
            string result = "{\"result\":\"Executed direct method: " + methodRequest.Name + "\"}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 200));
        }
        else
        {
            // Acknowledge the direct method call with a 400 error message.
            string result = "{\"result\":\"Invalid call\"}";
            return Task.FromResult(new MethodResponse(Encoding.UTF8.GetBytes(result), 400));
        }
    }

    static double DieRoll(double max)
    {
        return rand.NextDouble() * max;
    }

    static void UpdateTruck()
    {
        if (contents == ContentsEnum.empty)
        {
            // Turn the cooling system off, if possible, when the contents are empty.
            if (fan == FanEnum.on)
            {
                fan = FanEnum.off;
            }
            tempContents += -2.9 + DieRoll(6);
        }
        else
        {
            // Contents are full or melting.
            if (fan != FanEnum.failed)
            {
                if (tempContents < optimalTemperature - 5)
                {
                    // Turn the cooling system off, as contents are getting too cold.
                    fan = FanEnum.off;
                }
                else
                {
                    if (tempContents > optimalTemperature)
                    {
                        // Temp getting higher, turn cooling system back on.
                        fan = FanEnum.on;
                    }
                }

                // Randomly fail the cooling system.
                if (DieRoll(100) < 1)
                {
                    fan = FanEnum.failed;
                }
            }

            // Set the contents temperature. Maintaining a cooler temperature if the cooling system is on.
            if (fan == FanEnum.on)
            {
                tempContents += -3 + DieRoll(5);
            }
            else
            {
                tempContents += -2.9 + DieRoll(6);
            }

            // If the temperature is above a threshold, count the seconds this is occurring, and melt the contents if it goes on too long.
            if (tempContents >= tooWarmThreshold)
            {
                // Contents are warming.
                tooWarmPeriod += interval;

                if (tooWarmPeriod >= tooWarmtooLong)
                {
                    // Contents are melting.
                    contents = ContentsEnum.melting;
                }
            }
            else
            {
                // Contents are cooling.
                tooWarmPeriod = Math.Max(0, tooWarmPeriod - interval);
            }
        }

        timeOnCurrentTask += interval;

        switch (state)
        {
            case StateEnum.loading:
                if (timeOnCurrentTask >= loadingTime)
                {
                    // Finished loading.
                    state = StateEnum.ready;
                    contents = ContentsEnum.full;
                    timeOnCurrentTask = 0;

                    // Turn on the cooling fan.
                    // If the fan is in a failed state, assume it has been fixed, as it is at the base.
                    fan = FanEnum.on;
                    tempContents = -2;
                }
                break;

            case StateEnum.ready:
                timeOnCurrentTask = 0;
                break;

            case StateEnum.delivering:
                if (timeOnCurrentTask >= deliverTime)
                {
                    // Finished delivering.
                    contents = ContentsEnum.empty;
                    ReturnToBase();
                }
                break;

            case StateEnum.returning:

                // Update the truck position.
                UpdatePosition();

                // Check to see if the truck has arrived back at base.
                if (Arrived())
                {
                    switch (contents)
                    {
                        case ContentsEnum.empty:
                            state = StateEnum.loading;
                            break;

                        case ContentsEnum.full:
                            state = StateEnum.ready;
                            break;

                        case ContentsEnum.melting:
                            state = StateEnum.dumping;
                            break;
                    }
                    timeOnCurrentTask = 0;
                }
                break;

            case StateEnum.enroute:

                // Move the truck.
                UpdatePositionLocal();

                // Check to see if the truck has arrived at the customer.
                if (Arrived())
                {
                    state = StateEnum.delivering;
                    timeOnCurrentTask = 0;
                }
                break;

            case StateEnum.dumping:
                if (timeOnCurrentTask >= dumpingTime)
                {
                    // Finished dumping.
                    state = StateEnum.loading;
                    contents = ContentsEnum.empty;
                    timeOnCurrentTask = 0;
                }
                break;
        }
    }

    static void colorMessage(string text, ConsoleColor clr)
    {
        Console.ForegroundColor = clr;
        Console.WriteLine(text);
        Console.ResetColor();
    }
    static void greenMessage(string text)
    {
        colorMessage(text, ConsoleColor.Green);
    }

    static void redMessage(string text)
    {
        colorMessage(text, ConsoleColor.Red);
    }

    static async void SendTruckTelemetryAsync(Random rand, CancellationToken token)
    {
        while (true)
        {
            UpdateTruck();

            // Create the telemetry JSON message.
            var telemetryDataPoint = new
            {
                ContentsTemperature = Math.Round(tempContents, 2),
                TruckState = state.ToString(),
                CoolingSystemState = fan.ToString(),
                ContentsState = contents.ToString(),
                Location = new { lon = currentLon, lat = currentLat },
                Event = eventText,
            };
            var telemetryMessageString = JsonSerializer.Serialize(telemetryDataPoint);
            var telemetryMessage = new Message(Encoding.ASCII.GetBytes(telemetryMessageString));

            // Clear the events, as the message has been sent.
            eventText = noEvent;

            Console.WriteLine($"\nTelemetry data: {telemetryMessageString}");

            // Bail if requested.
            token.ThrowIfCancellationRequested();

            // Send the telemetry message.
            await s_deviceClient.SendEventAsync(telemetryMessage);
            greenMessage($"Telemetry sent {DateTime.Now.ToShortTimeString()}");

            await Task.Delay(intervalInMilliseconds);
        }
    }

    static async Task SendDevicePropertiesAsync()
    {
        reportedProperties["TruckID"] = truckIdentification;
        await s_deviceClient.UpdateReportedPropertiesAsync(reportedProperties);
        greenMessage($"Sent device properties: {JsonSerializer.Serialize(reportedProperties)}");
    }
    static async Task HandleSettingChanged(TwinCollection desiredProperties, object userContext)
    {
        string setting = "OptimalTemperature";
        if (desiredProperties.Contains(setting))
        {
            BuildAcknowledgement(desiredProperties, setting);
            optimalTemperature = (int) desiredProperties[setting];
            greenMessage($"Optimal temperature updated: {optimalTemperature}");
        }
        await s_deviceClient.UpdateReportedPropertiesAsync(reportedProperties);
    }

    static void BuildAcknowledgement(TwinCollection desiredProperties, string setting)
    {
        reportedProperties[setting] = new
        {
            value = desiredProperties[setting],
            status = "completed",
            desiredVersion = desiredProperties["$version"],
            message = "Processed"
        };
    }

#region LaunchMQTT
	    static MqttClient EdgeXAIclient;
	    static string AIData = "";
	    static string MqttBrokerIP = "127.0.0.1";
	    static string[] MqttTopic = { "MQTTExport" };
	    static string MQTTClientID;
	    static byte[] MqttQos = { MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE };
	    static string[] SupportItem = { "apple", "banana", "fork", "spoon" };
	    static void EdgeXAIclient_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e) {
		    try {
			    // logs("EdgeXAIclient_MqttMsgPublishReceived");
			    if(e.Topic == MqttTopic[0]) {
				    // logs("Received MQTT topic " + MqttTopic[0]);
				    var strJSON = Encoding.UTF8.GetString(e.Message);
				    if (strJSON.Contains("MQTTAnalyticservice")) {

					    // logs("MQTT received topic : " + e.Topic.ToString());
					    // logs("--------------------------------------------------------");
					    // logs(strJSON);
					    // logs("--------------------------------------------------------");

					    // gEdgeXAIObj = JsonConvert.DeserializeObject<EdgeXAIObj>(strJSON);

					    // if (gEdgeXAIObj.Readings[0].Value.Contains("objects")) {
					    // 	    var tmp = gEdgeXAIObj.Readings[0].Value.Replace('\"', '"');
					    // 	    var objtmp = JsonConvert.DeserializeObject<cAIValue>(tmp);
					    // 	    var detect_obj = objtmp.Objects[0].Roi_type;

					    // 	    foreach(string item in SupportItem) {
					    // 		    if(item == detect_obj) {
					    // 			    if (AIData != detect_obj) {
					    // 				    tbAIResult.Text = detect_obj;
					    // 				    tbAIResult.BackColor = Color.White;
					    // 				    LoadPicture(detect_obj);
					    // 				    AIData = detect_obj;
					    // 			    }
					    // 			    // else {logs("AI has same result don't redraw");}
					    // 		    }
					    // 		    // else {logs("We don't support this item, ignore it");}
					    // 	    }
					    // }
					    // else {logs(" SMS data, skip it.");}
				    }
				    // else {logs("useless data skip it.");}
			    }
			    // else {logs("shouldn't be here!!!");}
			    /*
			      tbStatusReport.Text = "ID : " + gEdgeXAIObj[0].ID + "\r\n" +
			      "Created : " + gEdgeXAIObj[0].Created.ToString() + "\r\n" +
			      "Origin : " + gEdgeXAIObj[0].Origin.ToString() + "\r\n" +
			      "Modified : " + gEdgeXAIObj[0].Modified.ToString() + "\r\n" +
			      "Device : " + gEdgeXAIObj[0].Device + "\r\n" +
			      "Name : " + gEdgeXAIObj[0].Name + "\r\n" +
			      "Value : " + gEdgeXAIObj[0].Value;
			      //"Value : " + gEdgeXAIObj[0].Value.Objects[0].Roi_type;
			      */
		    }
		    catch (Exception ex) {
			    // logs("=============Exception Occurs Start============= ");
			    // logs(ex.ToString());
			    // logs("==============Exception Occurs End============== ");
			    return;
		    }
	    }

	    static void LaunchMQTT() {
		    try {
			    // logs("LaunchMQTT");
			    EdgeXAIclient = new MqttClient(MqttBrokerIP);
			    //EdgeXAIclient = new MqttClient(IPAddress.Parse("127.0.0.1"));
			    EdgeXAIclient.MqttMsgPublishReceived += EdgeXAIclient_MqttMsgPublishReceived;
			    MQTTClientID = Guid.NewGuid().ToString();
			    EdgeXAIclient.Connect(MQTTClientID);
			    EdgeXAIclient.Subscribe(MqttTopic, MqttQos);
		    }
		    catch (Exception ex) {
			    // logs("=============Exception Occurs Start============= ");
			    // logs(ex.ToString());
			    // logs("==============Exception Occurs End============== ");
			    return;
		    }
	    }
#endregion

        static void Main(string[] args)
        {

            rand = new Random();
            colorMessage($"Starting {truckIdentification}", ConsoleColor.Yellow);

	    LaunchMQTT();

            currentLat = baseLat;
            currentLon = baseLon;

            // Connect to Azure Maps.
            azureMapsServices = new AzureMapsServices(AzureMapsKey);

            try
            {
                using (var security = new SecurityProviderSymmetricKey(DeviceID, PrimaryKey, null))
                {
                    DeviceRegistrationResult result = RegisterDeviceAsync(security).GetAwaiter().GetResult();
                    if (result.Status != ProvisioningRegistrationStatusType.Assigned)
                    {
                        Console.WriteLine("Failed to register device");
                        return;
                    }
                    IAuthenticationMethod auth = new DeviceAuthenticationWithRegistrySymmetricKey(result.DeviceId, (security as SecurityProviderSymmetricKey).GetPrimaryKey());
                    s_deviceClient = DeviceClient.Create(result.AssignedHub, auth, TransportType.Mqtt);
                }
                greenMessage("Device successfully connected to Azure IoT Central");

                SendDevicePropertiesAsync().GetAwaiter().GetResult();

                Console.Write("Register settings changed handler...");
                s_deviceClient.SetDesiredPropertyUpdateCallbackAsync(HandleSettingChanged, null).GetAwaiter().GetResult();
                Console.WriteLine("Done");

                cts = new CancellationTokenSource();

                // Create a handler for the direct method calls.
                s_deviceClient.SetMethodHandlerAsync("GoToCustomer", CmdGoToCustomer, null).Wait();
                s_deviceClient.SetMethodHandlerAsync("Recall", CmdRecall, null).Wait();

                SendTruckTelemetryAsync(rand, cts.Token);

                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                cts.Cancel();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine(ex.Message);
            }
        }


        public static async Task<DeviceRegistrationResult> RegisterDeviceAsync(SecurityProviderSymmetricKey security)
        {
            Console.WriteLine("Register device...");

            using (var transport = new ProvisioningTransportHandlerMqtt(TransportFallbackType.TcpOnly))
            {
                ProvisioningDeviceClient provClient =
                          ProvisioningDeviceClient.Create(GlobalDeviceEndpoint, ScopeID, security, transport);

                Console.WriteLine($"RegistrationID = {security.GetRegistrationID()}");

                Console.Write("ProvisioningClient RegisterAsync...");
                DeviceRegistrationResult result = await provClient.RegisterAsync();

                Console.WriteLine($"{result.Status}");

                return result;
            }
        }
    }
}
