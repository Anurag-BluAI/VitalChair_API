using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;

class UnifiedDataLogger
{
    private static SerialPort _serialPort;

    // Store recent values for smoothing
    private static Queue<int> pulseRateHistory = new Queue<int>();
    private static Queue<int> spo2History = new Queue<int>();

    // Smoothing window size
    private const int SmoothWindowSize = 5;

    // Store last captured values for unified display
    private static float lastTemperature1 = 0;
    private static float lastTemperature2 = 0;
    private static int lastPulseRate = 0;
    private static int lastPulseRate2 = 0;
    private static int lastSpO2 = 0;
    private static int lastSys = 0;
    private static int lastDia = 0;
    private static int lastMean = 0;


    static void Main()
    {
        InitializeSerialPort("/dev/verdin-uart1", 115200);


        

        try
        {
            _serialPort.Open();
            Console.WriteLine("Unified Data Logger Started (SPO2, Pulse Rate, Temperature & NIBP Frames)");
            SendRequestPOST();
            StartNiBP();


            var buffer = new byte[4096];
            while (true)
            {
                if (_serialPort.BytesToRead > 0)
                {
                    int bytesRead = _serialPort.Read(buffer, 0, buffer.Length);
                    ProcessRawData(buffer, bytesRead);
                }
                else
                {
                    Thread.Sleep(1); // Prevent CPU overuse
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _serialPort?.Close();
            Console.WriteLine("Monitoring Stopped");
        }
    }

    static void InitializeSerialPort(string portName, int baudRate)
    {
        _serialPort = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 100,
            WriteTimeout = 100,
            Parity = Parity.None,
            DataBits = 8,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadBufferSize = 8192,
            Encoding = System.Text.Encoding.Default
        };
    }

// Send RequestPOST frame (ID = 0x40, CHECKSUM = 0xC0) - Only once on startup
static void SendRequestPOST()
{
    byte[] postRequestFrame = new byte[] { 0x40, 0xC0 };
    try
    {
        _serialPort.Write(postRequestFrame, 0, postRequestFrame.Length);
        Console.WriteLine("RequestPOST frame sent successfully (Startup).");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending RequestPOST frame: {ex.Message}");
    }
}

// Send RequestPOST frame (ID = 0x40, CHECKSUM = 0xC0) - Only once on startup
static void StartNiBP()
{
    byte[] postRequestFrame = new byte[] { 0x55, 0xd5};
    try
    {
        _serialPort.Write(postRequestFrame, 0, postRequestFrame.Length);
        Console.WriteLine("Measuring Nibp Please relax.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending RequestPOST frame: {ex.Message}");
    }
}

    static void ProcessRawData(byte[] data, int length)
    {
        for (int i = 0; i < length - 7; i++)
        {
            // Detect TEMP frame (0x15) with 8-byte length
            if (data[i] == 0x15 && i + 8 <= length)
            {
                DecodeAndPrintTemperature(data, i);
                i += 7; // Skip processed TEMP bytes
            }
            // Detect SPO2 frame (0x17) with 7-byte length
            else if (data[i] == 0x17 && i + 7 <= length)
            {
                DecodeAndPrintSpo2(data, i);
                i += 6; // Skip processed SPO2 bytes
            }
            // Detect NIBP Result1 (0x22) with 9-byte length
            else if (data[i] == 0x22 && i + 9 <= length)
            {
                DecodeAndPrintNIBPResult1(data, i);
                i += 8; // Skip processed NIBP Result1 bytes
            }
            // Detect NIBP Result2 (0x23) with 5-byte length
            else if (data[i] == 0x23 && i + 5 <= length)
            {
                DecodeAndPrintNIBPResult2(data, i);
                i += 4; // Skip processed NIBP Result2 bytes
            }
        }
    }

    // Decode and print SPO2 and Pulse Rate
    static void DecodeAndPrintSpo2(byte[] data, int startIndex)
    {
        byte prHigh = data[startIndex + 4];   // Pulse rate high byte
        byte spo2Byte = data[startIndex + 5]; // SPO2 value

        // Calculate Pulse Rate (PR) - Mask lower 7 bits of prHigh
        int pulseRate = prHigh & 0x7F;

        // Calculate SPO2 - Mask lower 7 bits
        int spo2 = spo2Byte & 0x7F;

        // Apply range checks
        pulseRate = (pulseRate < 40 || pulseRate > 250) ? 0 : pulseRate;
        spo2 = (spo2 < 60 || spo2 > 100) ? 0 : spo2;

        // Apply smoothing
        lastPulseRate = SmoothValue(pulseRateHistory, pulseRate);
        lastSpO2 = SmoothValue(spo2History, spo2);

        PrintUnifiedData();
    }

    // Decode and print temperature values
    static void DecodeAndPrintTemperature(byte[] data, int startIndex)
    {
        byte temp1High = (byte)(data[startIndex + 3] & 0x7F);
        byte temp1Low = data[startIndex + 4];
        byte temp2High = (byte)(data[startIndex + 5] & 0x7F);
        byte temp2Low = data[startIndex + 6];

        // Combine high and low bytes for temperature calculation
        lastTemperature1 = ((temp1High << 8) | temp1Low) / 10.0f - 13.0f;
        lastTemperature2 = ((temp2High << 8) | temp2Low) / 10.0f - 13.0f;

        PrintUnifiedData();
    }

    // Decode and print NIBP Result1 (Systolic, Diastolic, Mean)
static void DecodeAndPrintNIBPResult1(byte[] data, int startIndex)
{
    byte head = data[startIndex + 1]; // HEAD byte

    // Systolic (DATA1 & DATA2)
    int sysHighBit = (head >> 0) & 0x01; // HEAD bit0 = DATA1's bit7
    int sysLowBit = (head >> 1) & 0x01;  // HEAD bit1 = DATA2's bit7
    int sysHigh = (sysHighBit << 7) | (data[startIndex + 2] & 0x7F);
    int sysLow = (sysLowBit << 7) | (data[startIndex + 3] & 0x7F);
    lastSys = (sysHigh << 8) | sysLow;

    // Diastolic (DATA3 & DATA4)
    int diaHighBit = (head >> 2) & 0x01; // HEAD bit2 = DATA3's bit7
    int diaLowBit = (head >> 3) & 0x01;  // HEAD bit3 = DATA4's bit7
    int diaHigh = (diaHighBit << 7) | (data[startIndex + 4] & 0x7F);
    int diaLow = (diaLowBit << 7) | (data[startIndex + 5] & 0x7F);
    lastDia = (diaHigh << 8) | diaLow;

    // Mean (DATA5 & DATA6)
    int meanHighBit = (head >> 4) & 0x01; // HEAD bit4 = DATA5's bit7
    int meanLowBit = (head >> 5) & 0x01;  // HEAD bit5 = DATA6's bit7
    int meanHigh = (meanHighBit << 7) | (data[startIndex + 6] & 0x7F);
    int meanLow = (meanLowBit << 7) | (data[startIndex + 7] & 0x7F);
    lastMean = (meanHigh << 8) | meanLow;

    // Validate ranges (0-300 mmHg)
    lastSys = (lastSys >= 0 && lastSys <= 300) ? lastSys : -100;
    lastDia = (lastDia >= 0 && lastDia <= 300) ? lastDia : -100;
    lastMean = (lastMean >= 0 && lastMean <= 300) ? lastMean : -100;

    PrintUnifiedData();
}

    // Decode and print NIBP Result2 (Pulse Rate)
static void DecodeAndPrintNIBPResult2(byte[] data, int startIndex)
{
    byte head = data[startIndex + 1]; // HEAD byte

    // Pulse Rate (DATA1 & DATA2)
    int prHighBit = (head >> 0) & 0x01; // HEAD bit0 = DATA1's bit7
    int prLowBit = (head >> 1) & 0x01;  // HEAD bit1 = DATA2's bit7
    int prHigh = (prHighBit << 7) | (data[startIndex + 2] & 0x7F);
    int prLow = (prLowBit << 7) | (data[startIndex + 3] & 0x7F);
    int pr = (prHigh << 8) | prLow;

    lastPulseRate2 = (pr >= 40 && pr <= 250) ? pr : -100;

    PrintUnifiedData();
}

    // Helper function for smoothing data using moving average
    static int SmoothValue(Queue<int> history, int newValue)
    {
        if (newValue == 0) return 0; // If invalid, return 0 immediately.

        history.Enqueue(newValue);
        if (history.Count > SmoothWindowSize) history.Dequeue();

        return (int)history.Average();
    }

    // Display combined output
    static void PrintUnifiedData()
    {
        Console.WriteLine($"Temp1: {lastTemperature1:F1}°C | Temp2: {lastTemperature2:F1}°C | Pulse Rate: {lastPulseRate} BPM | SpO2: {lastSpO2}% | Sys: {lastSys} mmHg | Dia: {lastDia} mmHg | Mean: {lastMean} mmHg | Pulse: {lastPulseRate2}");
    }
}
