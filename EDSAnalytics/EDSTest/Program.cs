﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Configuration;


namespace EDSAnalytics
{
    public class Program 
    {
        private static string port;
        private static string tenantId;
        private static string namespaceId;
        private static string apiVersion;

        public static void Main()
        {
            MainAsync().GetAwaiter().GetResult();
            Console.WriteLine();
            Console.WriteLine("Demo Application Ran Successfully!");
        }
        public static async Task<bool> MainAsync()
        {
            Console.WriteLine("Getting configuration from appsettings.json");
            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json");
            var configuration = builder.Build();

            // ==== Client constants ====
            port = configuration["edsPort"];
            tenantId = configuration["tenantId"];
            namespaceId = configuration["namespaceId"];
            apiVersion = configuration["apiVersion"];

            using (HttpClient httpClient = new HttpClient())
            {
                try
                {   // ====================== Data Filtering portion ======================
                    Console.WriteLine();
                    Console.WriteLine("================= Data Filtering =================");
                    // Step 1 - Create SineWave type
                    // create Timestamp property
                    SdsTypeProperty timestamp = new SdsTypeProperty
                    {
                        Id = "Timestamp",
                        Name = "Timestamp",
                        IsKey = true,
                        SdsType = new SdsType
                        {
                            Name = "DateTime",
                            SdsTypeCode = 16 // 16 is the SdsTypeCode for a DateTime type. Go to the SdsTypeCode section in EDS documentation for more information.
                        }
                    };
                    SdsType sineWaveType = new SdsType
                    {
                        Id = "SineWave",
                        Name = "SineWave",
                        SdsTypeCode = 1, // 1 is the SdsTypeCode for an object type. Go to the SdsTypeCode section in EDS documentation for more information.
                        Properties = new List<SdsTypeProperty>()
                        {
                            timestamp,
                            CreateSdsTypePropertyOfTypeDouble("Value", false)
                        }
                    };
                    await CreateType(sineWaveType);

                    // Step 2 - Create SineWave stream        
                    SdsStream sineWaveStream = await CreateStream(sineWaveType, "SineWave", "SineWave");

                    // Step 3 - Create a list of events of SineData objects. The value property of the SineData object is intitialized to a value between -1.0 and 1.0
                    Console.WriteLine("Initializing SineData Events");
                    List<SineData> waveList = new List<SineData>();
                    DateTime firstTimestamp = new DateTime();
                    firstTimestamp = DateTime.UtcNow;
                    // numberOfEvents must be an integer > 1
                    int numberOfEvents = 100;
                    for (int i = 0; i < numberOfEvents; i++)
                    {
                        SineData newEvent = new SineData(i)
                        {
                            Timestamp = firstTimestamp.AddSeconds(i).ToString("o")
                        };
                        waveList.Add(newEvent);
                    }
                    await WriteDataToStream(waveList, sineWaveStream);

                    // Step 4 - Ingress the sine wave data from the SineWave stream
                    var returnData = await IngressSineData(sineWaveStream, waveList[0].Timestamp, numberOfEvents);

                    // Step 5 - Create FilteredSineWaveStream
                    SdsStream filteredSineWaveStream = CreateStream(sineWaveType, "FilteredSineWave", "FilteredSineWave").Result;

                    // Step 6 - Populate FilteredSineWaveStream with filtered data
                    List<SineData> filteredWave = new List<SineData>();
                    int numberOfValidValues = 0;
                    Console.WriteLine("Filtering Data");
                    for (int i = 0; i < numberOfEvents; i++)
                    {
                        // filters the data to only include values outside the range -0.9 to 0.9 
                        // change this conditional to apply the type of filter you desire
                        if (returnData[i].Value > .9 || returnData[i].Value < -.9)
                        {
                            filteredWave.Add(returnData[i]);
                            numberOfValidValues++;
                        }
                    }
                    await WriteDataToStream(filteredWave, filteredSineWaveStream);

                    // ====================== Data Aggregation portion ======================
                    Console.WriteLine();
                    Console.WriteLine("================ Data Aggregation ================");
                    // Step 7 - Create aggregatedDataType type                  
                    SdsType aggregatedDataType = new SdsType
                    {
                        Id = "AggregatedData",
                        Name = "AggregatedData",
                        SdsTypeCode = 1,
                        Properties = new List<SdsTypeProperty>()
                        {
                            timestamp,
                            CreateSdsTypePropertyOfTypeDouble("Mean", false),
                            CreateSdsTypePropertyOfTypeDouble("Minimum", false),
                            CreateSdsTypePropertyOfTypeDouble("Maximum", false),
                            CreateSdsTypePropertyOfTypeDouble("Range", false)
                        }
                    };
                    await CreateType(aggregatedDataType);

                    // Step 8 - Create CalculatedAggregatedData stream
                    SdsStream calculatedAggregatedDataStream = await CreateStream(aggregatedDataType, "CalculatedAggregatedData", "CalculatedAggregatedData");

                    // Step 9 - Calculate mean, min, max, and range using c# libraries and send to the CalculatedAggregatedData Stream
                    Console.WriteLine("Calculating mean, min, max, and range");
                    var sineDataValues = new List<double>();
                    for (int i = 0; i < numberOfEvents; i++)
                    {
                        sineDataValues.Add(returnData[i].Value);
                        numberOfValidValues++;
                    }      
                    AggregateData calculatedData = new AggregateData
                    {
                        Timestamp = firstTimestamp.ToString("o"),
                        Mean = returnData.Average(rd => rd.Value),
                        Minimum = sineDataValues.Min(),
                        Maximum = sineDataValues.Max(),
                        Range = sineDataValues.Max()-sineDataValues.Min()
                    };
                    Console.WriteLine("    Mean = " + calculatedData.Mean);
                    Console.WriteLine("    Minimum = " + calculatedData.Minimum);
                    Console.WriteLine("    Maximum = " + calculatedData.Maximum);
                    Console.WriteLine("    Range = " + calculatedData.Range);
                    await WriteDataToStream(calculatedData, calculatedAggregatedDataStream);

                    // Step 10 - Create EdsApiAggregatedData stream
                    SdsStream edsApiAggregatedDataStream = CreateStream(aggregatedDataType, "EdsApiAggregatedData", "EdsApiAggregatedData").Result;

                    // Step 11 - Use EDS’s standard data aggregate API calls to ingress aggregated data calculated by EDS and send to EdsApiAggregatedData stream
                    string summaryData = await IngressSummaryData(sineWaveStream, calculatedData.Timestamp, firstTimestamp.AddMinutes(numberOfEvents).ToString("o"));
                    AggregateData edsApi = new AggregateData
                    {
                        Timestamp = firstTimestamp.ToString("o"),
                        Mean = GetValue(summaryData, "Mean"),
                        Minimum = GetValue(summaryData, "Minimum"),
                        Maximum = GetValue(summaryData, "Maximum"),
                        Range = GetValue(summaryData, "Range")
                    };
                    await WriteDataToStream(edsApi, edsApiAggregatedDataStream);
 
                    Console.WriteLine();
                    Console.WriteLine("==================== Clean-Up =====================");
                    
                    // Step 12 - Delete Streams and Types
                    await DeleteStream(sineWaveStream);
                    await DeleteStream(filteredSineWaveStream);
                    await DeleteStream(calculatedAggregatedDataStream);
                    await DeleteStream(edsApiAggregatedDataStream);
                    await DeleteType(sineWaveType);
                    await DeleteType(aggregatedDataType);                    
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.ToString());
                    return false;

                }
                finally
                {
                    (configuration as IDisposable)?.Dispose();
                }
            }
            return true;
        }

        private static void CheckIfResponseWasSuccessful(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(response.ToString());
            }
        }

        private static async Task DeleteStream(SdsStream stream)
        {
            HttpClient httpClient = new HttpClient();
            Console.WriteLine("Deleting " + stream.Id + " Stream");
            HttpResponseMessage responseDeleteStream =
                await httpClient.DeleteAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/{stream.Id}");
            CheckIfResponseWasSuccessful(responseDeleteStream);
        }

        private static async Task DeleteType(SdsType type)
        {
            HttpClient httpClient = new HttpClient();
            Console.WriteLine("Deleting " + type.Id + " Type");
            HttpResponseMessage responseDeleteType =
                await httpClient.DeleteAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Types/{type.Id}");
            CheckIfResponseWasSuccessful(responseDeleteType);
        }

        private static async Task<SdsStream> CreateStream(SdsType type, string id, string name) 
        {
            HttpClient httpClient = new HttpClient();
            SdsStream stream = new SdsStream
            {
                TypeId = type.Id,
                Id = id,
                Name = name
            };
            Console.WriteLine("Creating " + stream.Id + " Stream");
            StringContent stringStream = new StringContent(JsonSerializer.Serialize(stream));
            HttpResponseMessage responseCreateStream =
                await httpClient.PostAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/{stream.Id}", stringStream);
            CheckIfResponseWasSuccessful(responseCreateStream);
            return stream;
        }

        private static async Task CreateType(SdsType type)
        {
            HttpClient httpClient = new HttpClient();
            Console.WriteLine("Creating " + type.Id + " Type");
            StringContent stringType = new StringContent(JsonSerializer.Serialize(type));
            HttpResponseMessage responseType =
                await httpClient.PostAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Types/{type.Id}", stringType);
            CheckIfResponseWasSuccessful(responseType);

        }

        private static async Task<List<SineData>> IngressSineData(SdsStream stream, string timestamp, int numberOfEvents)
        {
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
            Console.WriteLine("Ingressing data from " + stream.Id + " stream");
            var responseIngress =
                await httpClient.GetAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/" +
                $"{stream.Id}/Data?startIndex={timestamp}&count={numberOfEvents}");
            CheckIfResponseWasSuccessful(responseIngress);
            MemoryStream ms = await DecompressGzip(responseIngress);
            using (var sr = new StreamReader(ms))
            {
                return await JsonSerializer.DeserializeAsync<List<SineData>>(ms);
            }
        }

        private static async Task<string> IngressSummaryData(SdsStream stream, string startTimestamp, string endTimestamp)
        {
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip");
            Console.WriteLine("Ingressing Data from " + stream.Id + " Stream Summary");
            var responseIngress =
                await httpClient.GetAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/" +
                $"{stream.Id}/Data/Summaries?startIndex={startTimestamp}&endIndex={endTimestamp}&count=1");
            CheckIfResponseWasSuccessful(responseIngress);
            MemoryStream ms = await DecompressGzip(responseIngress);
            using (var sr = new StreamReader(ms))
            {
                var objectSummaryData = await JsonSerializer.DeserializeAsync<object>(ms);
                return objectSummaryData.ToString().TrimStart(new char[] { '[' }).TrimEnd(new char[] { ']' }); 
            }
        }

        private static async Task<MemoryStream> DecompressGzip(HttpResponseMessage httpMessage)
        {
            var response = await httpMessage.Content.ReadAsStreamAsync();
            var destination = new MemoryStream();
            using (var decompressor = (Stream)new GZipStream(response, CompressionMode.Decompress, true))
            {
                decompressor.CopyToAsync(destination).Wait();
            }
            destination.Seek(0, SeekOrigin.Begin);
            return destination;
        }

        private static async Task WriteDataToStream(List<SineData> list, SdsStream stream)
        {
            HttpClient httpClient = new HttpClient();
            Console.WriteLine("Writing Data to " + stream.Id + " stream");
            StringContent serializedData = new StringContent(JsonSerializer.Serialize(list));
            HttpResponseMessage responseWriteDataToStream =
                await httpClient.PostAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/{stream.Id}/Data", serializedData);
            CheckIfResponseWasSuccessful(responseWriteDataToStream);
        }

        private static async Task WriteDataToStream(AggregateData data, SdsStream stream)
        {
            HttpClient httpClient = new HttpClient();
            List<AggregateData> dataList = new List<AggregateData>();
            dataList.Add(data);
            Console.WriteLine("Writing Data to " + stream.Id + " stream");
            StringContent serializedData = new StringContent(JsonSerializer.Serialize(dataList));
            HttpResponseMessage responseWriteDataToStream =
                await httpClient.PostAsync($"http://localhost:{port}/api/{apiVersion}/Tenants/{tenantId}/Namespaces/{namespaceId}/Streams/{stream.Id}/Data", serializedData);
            CheckIfResponseWasSuccessful(responseWriteDataToStream);
        }

        private static SdsTypeProperty CreateSdsTypePropertyOfTypeDouble(string idAndName, bool isKey)
        {
            SdsTypeProperty property = new SdsTypeProperty
            {
                Id = idAndName,
                Name = idAndName,
                IsKey = isKey,
                SdsType = new SdsType
                {
                    Name = "Double",
                    SdsTypeCode = 14 // 14 is the SdsTypeCode for a Double type. Go to the SdsTypeCode section in EDS documentation for more information.
                }
            };
            return property;
        }
     
        private static double GetValue(string json, string property)
        {
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement summaryElement = root.GetProperty("Summaries");
                if (summaryElement.TryGetProperty(property, out JsonElement propertyElement))
                {
                    if (propertyElement.TryGetProperty("Value", out JsonElement valueElement))
                    {
                        Console.WriteLine("    " + property + " = " + valueElement.ToString());
                        return Convert.ToDouble(valueElement.ToString());
                    }
                }
                return 0;
            }
        }
    }
}
