
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

class UnifiedDataLogger
{
    private static SerialPort _serialPortData;   // /dev/verdin-uart1 (binary sensor data)
    private static SerialPort _serialPortHeight; // /dev/verdin-uart2 (text height/weight data)
    private static readonly object _lock = new object(); // For thread-safe data updates

    private static Queue<int> pulseRateHistory = new Queue<int>();
    private static Queue<int> spo2History = new Queue<int>();
    private static readonly int SmoothWindowSize = 5;

    // API URLs and key
    private static readonly string BluHealthVitalsUrl = "https://bluhealth.bluai.ai/api/vitals";
    private static readonly string AzureOpenAiUrl = "https://bluai-azureopenai.openai.azure.com/openai/deployments/gpt-4o-mini/chat/completions?api-version=2024-08-01-preview";
    private static readonly string AzureApiKey = "3e502b17237c44bf9a7c61ecf012e7d2";

    // Current live values
    private static float lastTemperature1 = 0;
    private static float lastTemperature2 = 0;
    private static int lastPulseRate = 0;
    private static int lastPulseRate2 = 0;
    private static int lastSpO2 = 0;
    private static int lastSys = 0;
    private static int lastDia = 0;
    private static int lastMean = 0;
    private static float lastHeight = -1;
    private static float lastWeight = -1;

    // Stored values for SUBMIT
    private static float storedTemperature1 = 0;
    private static float storedTemperature2 = 0;
    private static float storedHeight = -1;
    private static float storedWeight = -1;
    private static float storedBMI = -1;
    private static int storedSpO2 = 0;
    private static int storedPulseRate = 0;
    private static int storedSys = 0;
    private static int storedDia = 0;
    private static int storedMean = 0;
    private static int storedPulseRate2 = 0;

    // State machine
    private enum MeasurementState
    {
        IDLE,
        HEIGHT_WEIGHT,
        TEMPERATURE,
        SPO2,
        NIBP,
        DONE
    }
    private static MeasurementState _currentState = MeasurementState.IDLE;
    private static bool _isLiveMode = false;
    private static bool _isNIBPActive = false;

    static void Main()
    {
        InitializeSerialPorts();

        try
        {
            _serialPortData.Open();
            _serialPortHeight.Open();

            Console.WriteLine("Unified Data Logger Started");
            Console.WriteLine("Commands: START, NEXT, BACK, START BP, STOP BP, DONE, SUBMIT, HOME, INSIGHTS, LIVE");

            // Start command reading thread
            Thread commandThread = new Thread(ReadCommands);
            commandThread.IsBackground = true;
            commandThread.Start();

            // Start height/weight reading thread
            Thread heightThread = new Thread(ReadHeightLoop);
            heightThread.IsBackground = true;
            heightThread.Start();

            var buffer = new byte[4096];
            while (true)
            {
                try
                {
                    int bytesRead = _serialPortData.Read(buffer, 0, buffer.Length);
                    ProcessRawData(buffer, bytesRead);
                }
                catch (TimeoutException)
                {
                    Thread.Sleep(10); // Prevent CPU overuse
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UART1 error: {ex.Message}");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _serialPortData?.Close();
            _serialPortHeight?.Close();
            Console.WriteLine("Monitoring Stopped");
        }
    }

    static void InitializeSerialPorts()
    {
        _serialPortData = new SerialPort("/dev/verdin-uart1", 115200)
        {
            ReadTimeout = 500,
            WriteTimeout = 100,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadBufferSize = 8192,
            Encoding = System.Text.Encoding.Default
        };

        _serialPortHeight = new SerialPort("/dev/verdin-uart2", 115200)
        {
            ReadTimeout = 500,
            WriteTimeout = 100,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadBufferSize = 8192,
            Encoding = System.Text.Encoding.ASCII
        };
    }

    static void ReadCommands()
    {
        while (true)
        {
            string command = Console.ReadLine()?.Trim().ToUpper();
            if (string.IsNullOrEmpty(command)) continue;

            lock (_lock)
            {
                switch (command)
                {
                    case "START":
                        if (_currentState == MeasurementState.IDLE && !_isLiveMode)
                        {
                            _currentState = MeasurementState.HEIGHT_WEIGHT;
                            Console.WriteLine("Starting Height/Weight measurement...");
                            SendHeightWeightCommand();
                        }
                        break;
                    case "NEXT":
                        if (!_isLiveMode)
                        {
                            if (_currentState == MeasurementState.HEIGHT_WEIGHT)
                            {
                                StoreHeightWeight();
                                _currentState = MeasurementState.TEMPERATURE;
                                Console.WriteLine("Starting Body Temperature measurement...");
                                SendRequestPOST();
                            }
                            else if (_currentState == MeasurementState.TEMPERATURE)
                            {
                                StoreTemperature();
                                _currentState = MeasurementState.SPO2;
                                Console.WriteLine("Starting SpO2 measurement...");
                                SendRequestPOST();
                            }
                            else if (_currentState == MeasurementState.SPO2)
                            {
                                StoreSpO2();
                                _currentState = MeasurementState.NIBP;
                                Console.WriteLine("Ready for NIBP measurement. Use 'START BP' to begin.");
                            }
                        }
                        break;
                    case "BACK":
                        if (!_isLiveMode)
                        {
                            if (_currentState == MeasurementState.TEMPERATURE)
                            {
                                _currentState = MeasurementState.HEIGHT_WEIGHT;
                                Console.WriteLine("Returning to Height/Weight measurement...");
                                SendHeightWeightCommand();
                            }
                            else if (_currentState == MeasurementState.SPO2)
                            {
                                _currentState = MeasurementState.TEMPERATURE;
                                Console.WriteLine("Returning to Body Temperature measurement...");
                                SendRequestPOST();
                            }
                            else if (_currentState == MeasurementState.NIBP)
                            {
                                _currentState = MeasurementState.SPO2;
                                Console.WriteLine("Returning to SpO2 measurement...");
                                SendRequestPOST();
                            }
                        }
                        break;
                    case "START BP":
                        if (_currentState == MeasurementState.NIBP || _isLiveMode)
                        {
                            _isNIBPActive = true;
                            StartNiBP();
                        }
                        break;
                    case "STOP BP":
                        if (_currentState == MeasurementState.NIBP || _isLiveMode)
                        {
                            _isNIBPActive = false;
                            Console.WriteLine("Stopped NIBP measurement.");
                        }
                        break;
                    case "DONE":
                        if (_currentState == MeasurementState.NIBP && !_isLiveMode)
                        {
                            StoreNIBP();
                            _currentState = MeasurementState.DONE;
                            Console.WriteLine("Measurements complete. Use 'SUBMIT', 'INSIGHTS', or 'HOME'.");
                        }
                        break;
                    case "SUBMIT":
                        if (_currentState == MeasurementState.DONE && !_isLiveMode)
                        {
                            PrintStoredData();
                            SendVitalsToBluHealth().GetAwaiter().GetResult();
                        }
                        break;
                    case "HOME":
                        if (_currentState == MeasurementState.DONE && !_isLiveMode)
                        {
                            _currentState = MeasurementState.HEIGHT_WEIGHT;
                            ResetStoredValues();
                            Console.WriteLine("Returning to Height/Weight measurement...");
                            SendHeightWeightCommand();
                        }
                        break;
                    case "INSIGHTS":
                        if (_currentState == MeasurementState.DONE && !_isLiveMode)
                        {
                            GetAzureInsights().GetAwaiter().GetResult();
                        }
                        break;
                    case "LIVE":
                        _isLiveMode = true;
                        _currentState = MeasurementState.IDLE;
                        Console.WriteLine("Starting live mode. All vitals running. Use 'START BP' for NIBP.");
                        SendRequestPOST();
                        SendHeightWeightCommand();
                        break;
                }
            }
        }
    }

    static void SendHeightWeightCommand()
    {
        // Placeholder: Send command to height/weight device if required
        Console.WriteLine("Height/Weight measurement initiated.");
    }

    static void ReadHeightLoop()
    {
        while (true)
        {
            try
            {
                string line = _serialPortHeight.ReadLine();
                if (_currentState != MeasurementState.HEIGHT_WEIGHT && !_isLiveMode) continue;

                if (line.StartsWith("HEIGHT:"))
                {
                    string value = line.Replace("HEIGHT:", "").Replace("cm", "").Trim();
                    if (float.TryParse(value, out float parsedHeight))
                    {
                        lock (_lock)
                        {
                            lastHeight = parsedHeight;
                            if (_currentState == MeasurementState.HEIGHT_WEIGHT)
                            {
                                PrintHeightWeight();
                            }
                            else if (_isLiveMode)
                            {
                                PrintLiveData();
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[Parse Error] Failed to parse height: {value}");
                    }
                }
                else if (line.StartsWith("WEIGHT:"))
                {
                    string value = line.Replace("WEIGHT:", "").Replace("kg", "").Replace("(measuring)", "").Replace("(locked)", "").Replace("(reset)", "").Replace("(error)", "").Trim();
                    if (float.TryParse(value, out float parsedWeight))
                    {
                        lock (_lock)
                        {
                            lastWeight = parsedWeight;
                            if (_currentState == MeasurementState.HEIGHT_WEIGHT)
                            {
                                PrintHeightWeight();
                            }
                            else if (_isLiveMode)
                            {
                                PrintLiveData();
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[Parse Error] Failed to parse weight: {value}");
                    }
                }
            }
            catch (TimeoutException)
            {
                // No data, continue looping
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UART2 error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    static void SendRequestPOST()
    {
        byte[] postRequestFrame = new byte[] { 0x40, 0xC0 };
        try
        {
            _serialPortData.Write(postRequestFrame, 0, postRequestFrame.Length);
            Console.WriteLine("RequestPOST frame sent successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending RequestPOST frame: {ex.Message}");
        }
    }

    static void StartNiBP()
    {
        byte[] nibpStartFrame = new byte[] { 0x55, 0xD5 };
        try
        {
            _serialPortData.Write(nibpStartFrame, 0, nibpStartFrame.Length);
            Console.WriteLine("Started NIBP Measurement.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting NIBP: {ex.Message}");
        }
    }

    static void ProcessRawData(byte[] data, int length)
    {
        for (int i = 0; i < length - 7; i++)
        {
            if (data[i] == 0x15 && i + 8 <= length && (_isLiveMode || _currentState == MeasurementState.TEMPERATURE || _currentState == MeasurementState.SPO2))
            {
                DecodeAndPrintTemperature(data, i);
                i += 7;
            }
            else if (data[i] == 0x17 && i + 7 <= length && (_isLiveMode || _currentState == MeasurementState.SPO2))
            {
                DecodeAndPrintSpo2(data, i);
                i += 6;
            }
            else if (data[i] == 0x22 && i + 9 <= length && (_isLiveMode || _currentState == MeasurementState.NIBP) && _isNIBPActive)
            {
                DecodeAndPrintNIBPResult1(data, i);
                i += 8;
            }
            else if (data[i] == 0x23 && i + 5 <= length && (_isLiveMode || _currentState == MeasurementState.NIBP) && _isNIBPActive)
            {
                DecodeAndPrintNIBPResult2(data, i);
                i += 4;
            }
        }
    }

    static void DecodeAndPrintTemperature(byte[] data, int startIndex)
    {
        byte temp1High = (byte)(data[startIndex + 3] & 0x7F);
        byte temp1Low = data[startIndex + 4];
        byte temp2High = (byte)(data[startIndex + 5] & 0x7F);
        byte temp2Low = data[startIndex + 6];

        lock (_lock)
        {
            lastTemperature1 = ((temp1High << 8) | temp1Low) / 10.0f - 13.0f;
            lastTemperature2 = ((temp2High << 8) | temp2Low) / 10.0f - 13.0f;
            if (_currentState == MeasurementState.TEMPERATURE)
            {
                PrintTemperature();
            }
            else if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    static void DecodeAndPrintSpo2(byte[] data, int startIndex)
    {
        byte prHigh = data[startIndex + 4];
        byte spo2Byte = data[startIndex + 5];

        int pulseRate = prHigh & 0x7F;
        int spo2 = spo2Byte & 0x7F;

        pulseRate = (pulseRate < 40 || pulseRate > 250) ? 0 : pulseRate;
        spo2 = (spo2 < 60 || spo2 > 100) ? 0 : spo2;

        lock (_lock)
        {
            lastPulseRate = SmoothValue(pulseRateHistory, pulseRate);
            lastSpO2 = SmoothValue(spo2History, spo2);
            if (_currentState == MeasurementState.SPO2)
            {
                Console.WriteLine($"SpO2: {lastSpO2}% | Pulse: {lastPulseRate} BPM");
            }
            else if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    static void DecodeAndPrintNIBPResult1(byte[] data, int startIndex)
    {
        byte head = data[startIndex + 1];

        int sysHigh = ((head >> 0) & 0x01) << 7 | (data[startIndex + 2] & 0x7F);
        int sysLow = ((head >> 1) & 0x01) << 7 | (data[startIndex + 3] & 0x7F);
        int diaHigh = ((head >> 2) & 0x01) << 7 | (data[startIndex + 4] & 0x7F);
        int diaLow = ((head >> 3) & 0x01) << 7 | (data[startIndex + 5] & 0x7F);
        int meanHigh = ((head >> 4) & 0x01) << 7 | (data[startIndex + 6] & 0x7F);
        int meanLow = ((head >> 5) & 0x01) << 7 | (data[startIndex + 7] & 0x7F);

        lock (_lock)
        {
            lastSys = (sysHigh << 8) | sysLow;
            lastDia = (diaHigh << 8) | diaLow;
            lastMean = (meanHigh << 8) | meanLow;

            lastSys = (lastSys >= 0 && lastSys <= 300) ? lastSys : -100;
            lastDia = (lastDia >= 0 && lastDia <= 300) ? lastDia : -100;
            lastMean = (lastMean >= 0 && lastMean <= 300) ? lastMean : -100;

            if (_currentState == MeasurementState.NIBP)
            {
                Console.WriteLine($"Sys: {lastSys} mmHg | Dia: {lastDia} mmHg | Mean: {lastMean} mmHg");
            }
            else if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    static void DecodeAndPrintNIBPResult2(byte[] data, int startIndex)
    {
        byte head = data[startIndex + 1];

        int prHigh = ((head >> 0) & 0x01) << 7 | (data[startIndex + 2] & 0x7F);
        int prLow = ((head >> 1) & 0x01) << 7 | (data[startIndex + 3] & 0x7F);
        int pr = (prHigh << 8) | prLow;

        lock (_lock)
        {
            lastPulseRate2 = (pr >= 40 && pr <= 250) ? pr : -100;
            if (_currentState == MeasurementState.NIBP)
            {
                Console.WriteLine($"Pulse2: {lastPulseRate2} BPM");
            }
            else if (_isLiveMode)
            {
                PrintLiveData();
            }
        }
    }

    static void StoreHeightWeight()
    {
        lock (_lock)
        {
            storedHeight = lastHeight;
            storedWeight = lastWeight;
            if (storedHeight > 0 && storedWeight > 0)
            {
                float heightInMeters = storedHeight / 100.0f;
                storedBMI = storedWeight / (heightInMeters * heightInMeters);
            }
        }
    }

    static void StoreTemperature()
    {
        lock (_lock)
        {
            storedTemperature1 = lastTemperature1;
            storedTemperature2 = lastTemperature2;
        }
    }

    static void StoreSpO2()
    {
        lock (_lock)
        {
            storedSpO2 = lastSpO2;
            storedPulseRate = lastPulseRate;
        }
    }

    static void StoreNIBP()
    {
        lock (_lock)
        {
            storedSys = lastSys;
            storedDia = lastDia;
            storedMean = lastMean;
            storedPulseRate2 = lastPulseRate2;
        }
    }

    static void ResetStoredValues()
    {
        lock (_lock)
        {
            storedTemperature1 = 0;
            storedTemperature2 = 0;
            storedHeight = -1;
            storedWeight = -1;
            storedBMI = -1;
            storedSpO2 = 0;
            storedPulseRate = 0;
            storedSys = 0;
            storedDia = 0;
            storedMean = 0;
            storedPulseRate2 = 0;
        }
    }

    static void PrintHeightWeight()
    {
        lock (_lock)
        {
            float bmi = -1;
            if (lastHeight > 0 && lastWeight > 0)
            {
                float heightInMeters = lastHeight / 100.0f;
                bmi = lastWeight / (heightInMeters * heightInMeters);
            }
            Console.WriteLine($"Height: {lastHeight:F1} cm | Weight: {lastWeight:F1} kg | BMI: {(bmi >= 0 ? bmi.ToString("F1") : "-1")}");
        }
    }

    static void PrintTemperature()
    {
        lock (_lock)
        {
            Console.WriteLine($"Temp1: {lastTemperature1:F1}°C | Temp2: {lastTemperature2:F1}°C");
        }
    }

    static void PrintLiveData()
    {
        lock (_lock)
        {
            float bmi = -1;
            if (lastHeight > 0 && lastWeight > 0)
            {
                float heightInMeters = lastHeight / 100.0f;
                bmi = lastWeight / (heightInMeters * heightInMeters);
            }
            Console.WriteLine(
                $"Temp1: {lastTemperature1:F1}°C | Temp2: {lastTemperature2:F1}°C | " +
                $"Pulse1: {lastPulseRate} BPM | SpO2: {lastSpO2}% | " +
                $"Sys: {(_isNIBPActive ? lastSys.ToString() : "-")} mmHg | Dia: {(_isNIBPActive ? lastDia.ToString() : "-")} mmHg | " +
                $"Mean: {(_isNIBPActive ? lastMean.ToString() : "-")} mmHg | Pulse2: {(_isNIBPActive ? lastPulseRate2.ToString() : "-")} BPM | " +
                $"Height: {lastHeight:F1} cm | Weight: {lastWeight:F1} kg | BMI: {(bmi >= 0 ? bmi.ToString("F1") : "-1")}"
            );
        }
    }

    static void PrintStoredData()
    {
        lock (_lock)
        {
            Console.WriteLine("Final Measurement Results:");
            Console.WriteLine($"Height: {storedHeight:F1} cm | Weight: {storedWeight:F1} kg | BMI: {(storedBMI >= 0 ? storedBMI.ToString("F1") : "-1")}");
            Console.WriteLine($"Body Temperature: Temp1: {storedTemperature1:F1}°C | Temp2: {storedTemperature2:F1}°C");
            Console.WriteLine($"SpO2: {storedSpO2}% | Pulse: {storedPulseRate} BPM");
            Console.WriteLine($"Sys: {storedSys} mmHg | Dia: {storedDia} mmHg | Mean: {storedMean} mmHg | Pulse2: {storedPulseRate2} BPM");
        }
    }

    static int SmoothValue(Queue<int> history, int newValue)
    {
        if (newValue == 0) return 0;

        history.Enqueue(newValue);
        if (history.Count > SmoothWindowSize)
            history.Dequeue();

        return (int)history.Average();
    }

    static async Task SendVitalsToBluHealth()
    {
        using HttpClient httpClient = new HttpClient();

        // Prepare the vitals data payload
        var payload = new
        {
            patient_id = 2830,
            body_temperature = storedTemperature1.ToString("F1"),
            body_temperature2 = storedTemperature2.ToString("F1"),
            IR_temperature = "0", // Not measured
            pulse_rate = storedPulseRate.ToString(),
            respiration_rate = "0", // Not measured
            blood_pressure_systolic = storedSys.ToString(),
            blood_pressure_diastolic = storedDia.ToString(),
            blood_oxygen = storedSpO2.ToString(),
            blood_glucose_level = "0", // Not measured
            bmi = storedBMI >= 0 ? storedBMI.ToString("F1") : "0"
        };

        string jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        try
        {
            // Send data to BluHealth
            HttpResponseMessage response = await httpClient.PostAsync(BluHealthVitalsUrl, content);
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Vitals data sent successfully to BluHealth!");
            }
            else
            {
                Console.WriteLine($"Vitals request failed. Status: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending vitals to BluHealth: {ex.Message}");
        }
    }

    static async Task GetAzureInsights()
    {
        // Prepare the vitals data payload
        var payload = new
        {
            patient_id = 2822,
            body_temperature = storedTemperature1.ToString("F1"),
            body_temperature2 = storedTemperature2.ToString("F1"),
            IR_temperature = "0",
            pulse_rate = storedPulseRate.ToString(),
            respiration_rate = "0",
            blood_pressure_systolic = storedSys.ToString(),
            blood_pressure_diastolic = storedDia.ToString(),
            blood_oxygen = storedSpO2.ToString(),
            blood_glucose_level = "0",
            bmi = storedBMI >= 0 ? storedBMI.ToString("F1") : "0"
        };

        string jsonPayload = JsonSerializer.Serialize(payload);

        try
        {
            // Analyze data using Azure OpenAI
            string azureResponse = await SendToAzureOpenAi(AzureOpenAiUrl, AzureApiKey, jsonPayload);
            if (!string.IsNullOrEmpty(azureResponse))
            {
                Console.WriteLine("AI Analysis Response:");
                string filteredResponse = FilterAzureResponse(azureResponse);
                Console.WriteLine(filteredResponse);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving Azure insights: {ex.Message}");
        }
    }

    static async Task<string> SendToAzureOpenAi(string url, string apiKey, string payload)
    {
        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("api-key", apiKey);

        // Prepare the request body
        var requestBody = new
        {
            messages = new[]
            {
                new { role = "system", content = "You are an AI medical assistant. Analyze the patient's vital data and provide recommendations." },
                new { role = "user", content = payload }
            },
            max_tokens = 1000,
            temperature = 0.8
        };

        string jsonRequest = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await httpClient.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                string rawContent = await response.Content.ReadAsStringAsync();
                return rawContent;
            }
            else
            {
                Console.WriteLine($"Azure OpenAI request failed. Status: {response.StatusCode}");
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending data to Azure OpenAI: {ex.Message}");
            return string.Empty;
        }
    }

    static string FilterAzureResponse(string rawContent)
    {
        if (string.IsNullOrEmpty(rawContent)) return "No insights available.";

        // Clean the response
        string cleanedContent = rawContent;
        cleanedContent = Regex.Replace(cleanedContent, @"```json", "");
        cleanedContent = Regex.Replace(cleanedContent, @"```", "");
        cleanedContent = Regex.Replace(cleanedContent, @",\s*}", "}");
        cleanedContent = Regex.Replace(cleanedContent, @"\""(.*?)\"":\s*\""(.*?)\""(?!\s)", "\"$1\":\"$2\"");

        try
        {
            var responseJson = JsonSerializer.Deserialize<JsonElement>(cleanedContent);
            var content = responseJson.GetProperty("choices")[0].GetProperty("message").GetProperty("content").ToString();

            // Extract relevant sections
            var vitalDataAnalysis = ExtractSection(content, "Vital Data Analysis");
            var recommendations = ExtractSection(content, "Recommendations");
            var conclusion = ExtractSection(content, "Conclusion");

            return $"Health Insights:\n{vitalDataAnalysis}\n\nRecommendations:\n{recommendations}\n\nConclusion:\n{conclusion}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing Azure response: {ex.Message}");
            return "Failed to parse insights.";
        }
    }

    static string ExtractSection(string content, string sectionTitle)
    {
        var regex = new Regex($@"### {sectionTitle}:(.*?)###", RegexOptions.Singleline);
        var match = regex.Match(content);
        return match.Success ? match.Groups[1].Value.Trim() : "No data available.";
    }
}