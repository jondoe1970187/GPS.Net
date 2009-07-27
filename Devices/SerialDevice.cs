﻿using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
#if !PocketPC
using System.ComponentModel;
using System.Runtime.ConstrainedExecution;
using System.Security.Permissions;
#endif
using System.Threading;
using Microsoft.Win32;
using GeoFramework.Gps.Nmea;

namespace GeoFramework.Gps.IO
{
    /// <summary>
    /// Represents a serial (RS-232) device.
    /// </summary>
    /// <remarks><para>This class is used to connect to any device using the RS-232 protocol.  Desktop computers will typically 
    /// have at least one serial port, and in some cases a "virtual" serial port is created to make a device emulate RS-232.  For
    /// example, since there is no USB standard for GPS devices, USB GPS device manufacturers typically provide a special "USB-to-serial"
    /// driver to make the device available for third-party applications.</para>
    /// <para>Each serial port on a computer has a unique name, typically beginning with "COM" followed by a number, then a colon
    /// (e.g. COM5:).  In some special circumstances, such as the GPS Intermediate Driver on Windows Mobile devices, a different prefix
    /// is used.  Each serial port includes other configuration settings, most importantly the baud rate, which controls the speed of
    /// communications.  GPS device manufacturers must support 4800 baud in order to comply with the NMEA-0183 standard, but many newer devices
    /// use faster speeds.  The baud rate of a connection must be specified precisely, otherwise all data from the device will be
    /// unrecognizable.</para>
    /// <para>Other settings for serial ports are the data bits, stop bits, and parity.  In the context of GPS, a vast majority of GPS 
    /// devices use eight data bits, one stop bit, and no parity.  these settings are used if no settings are explicitly provided.</para>
    /// </remarks>
#if !PocketPC
    [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
    [RegistryPermission(SecurityAction.LinkDemand, ViewAndModify = @"HKEY_LOCAL_MACHINE\SOFTWARE\GeoFrameworks\GPS.NET\3.0\Devices\Bluetooth")]
#endif
    public class SerialDevice : Device, IEquatable<SerialDevice>
    {
#if !PocketPC
        /* We can use .NET's SerialPort for all serial communications without
         * any problems. 
         */
        private SerialPort _Port;
#else
        /* Older devices run into problems when trying to use .NET's SerialPort class.
         * For example, it will barf trying to open the GPS Intermediate Driver because
         * the driver may *temporarily* report error #21 until the underlying GPS port
         * is opened.  So, we need a custom SerialStream class for this case.
         */
        private string _Port;
        private int _BaudRate;
#endif
        private int _LastSuccessfulBaudRate;
        private string _Name;

        private static int _MaximumAllowedFailures = 100;
        private static IList<int> _DetectionBaudRates = new List<int>(new int[] { 115200, 57600, 38400, 19200, 9600, 4800 });

        #region Constants

        private const string RootKeyName = @"SOFTWARE\GeoFrameworks\GPS.NET\3.0\Devices\Serial\";

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        public SerialDevice()
        {
#if !PocketPC
            _Port = new SerialPort();
#endif
        }

        /// <summary>
        /// Creates a new instance using the specified port.
        /// </summary>
        /// <param name="portName"></param>
        public SerialDevice(string portName)
            : this(portName, 4800)
        { }

        /// <summary>
        /// Creates a new instance using the specified port name and baud rate.
        /// </summary>
        /// <param name="portName"></param>
        /// <param name="baudRate"></param>
        public SerialDevice(string portName, int baudRate)
        { 
#if !PocketPC
            _Port = new SerialPort(portName, baudRate);
            _Port.ReadTimeout = (int)DefaultReadTimeout.TotalMilliseconds;
            _Port.WriteTimeout = (int)DefaultWriteTimeout.TotalMilliseconds;
            _Port.NewLine = "\r\n";
            _Port.WriteBufferSize = NmeaReader.IdealNmeaBufferSize;
            _Port.ReadBufferSize = NmeaReader.IdealNmeaBufferSize; 
            _Port.ReceivedBytesThreshold = 65535;  // We don't need this event, so max out the threshold
            _Port.Encoding = ASCIIEncoding.ASCII;
#else
            _Port = portName;
            _BaudRate = baudRate;
#endif

            // Default to the port name for the friendly name
            _Name = portName;
            
            // Read the cache
            OnCacheRead();
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Returns the name of the port used to open a connection.
        /// </summary>
#if !PocketPC
        [Category("Data")]
        [Description("Returns the name of the port used to open a connection.")]
        [Browsable(true)]
        [ParenthesizePropertyName(true)]
#endif
        public virtual string Port
        {
            get
            {
#if !PocketPC
                return _Port.PortName;
#else
                return _Port;
#endif
            }
            set
            {
#if !PocketPC
                _Port.PortName = value;
#else
                _Port = value;
#endif
            }
        }

        /// <summary>
        /// Returns the numeric portion of the port name.
        /// </summary>
#if !PocketPC
        [Category("Data")]
        [Description("Returns the numeric portion of the port name.")]
        [Browsable(true)]
#endif
        public int PortNumber
        {
            get
            {
                StringBuilder numericPortion = new StringBuilder(4);

                // Extract numeric digits from the name
#if !PocketPC
                foreach (char character in _Port.PortName)
#else
                foreach (char character in _Port)
#endif
                {
                    if (char.IsNumber(character))
                        numericPortion.Append(character);
                }

                // Yes.  Extract the number
                return int.Parse(numericPortion.ToString());
            }
        }

        /// <summary>
        /// Controls the speed of communications for this device.
        /// </summary>
        /// <remarks>This property controls the speed of read and write operations for the device.  The baud rate specified must precisely
        /// match a rate supported by the device, otherwise data will be unrecognizable.  GPS devices are required to support a minimum baud rate of 4800
        /// in order to comply with the NMEA-0183 standard (though not all devices do this).  A higher rate is preferable.</remarks>
#if !PocketPC
        [Category("Data")]
        [Description("Controls the speed of communications for this device.")]
        [Browsable(true)]
#endif
        public int BaudRate
        {
            get
            {
#if !PocketPC
                return _Port.BaudRate;
#else
                return _BaudRate;
#endif
            }
            set
            {
#if !PocketPC
                _Port.BaudRate = value;
#else
                _BaudRate = value;
#endif
            }
        }

        #endregion

        #region Overrides

        public override string Name
        {
            get
            {
                return _Name;
            }
        }

        public override bool AllowConnections
        {
            get
            {
                return base.AllowConnections && Devices.AllowSerialConnections;
            }
            set
            {
                base.AllowConnections = value;
            }
        }

        public override void Reset()
        {
            // Clear out the stream
            base.Reset();

#if !PocketPC
            // Clone the port
            SerialPort clone = new SerialPort(_Port.PortName, _Port.BaudRate, _Port.Parity, _Port.DataBits, _Port.StopBits);
            clone.ReadTimeout = _Port.ReadTimeout;
            clone.WriteTimeout = _Port.WriteTimeout;
            clone.NewLine = _Port.NewLine;
            clone.WriteBufferSize = _Port.WriteBufferSize;
            clone.ReadBufferSize = _Port.ReadBufferSize;
            clone.ReceivedBytesThreshold = _Port.ReceivedBytesThreshold;
            clone.Encoding = _Port.Encoding;

            /* Try to dispose of the old base stream.  I got Win32 errors in some cases
             * because the Finalizer of a SerialPort's SerialStream was called, and (the fucking bastards)
             * they don't catch exceptions in their finalizer  >.<.   .NET fail 
             */
            if (_Port.IsOpen)
            {
                try { _Port.BaseStream.Close(); }
                catch { }
#if Framework30
                try { _Port.BaseStream.Dispose(); } catch { }
#endif
                try { _Port.Dispose(); } catch { }
            }

            // Use this new reference.
            _Port = clone;
#endif
        }

        protected override Stream OpenStream(FileAccess access, FileShare sharing)
        {
#if !PocketPC
            // Open the port if it's not already open
            if (!_Port.IsOpen)
                _Port.Open();

            return _Port.BaseStream;
#else
            return new SerialStream(_Port, _BaudRate, access, sharing);
#endif
        }

#if !PocketPC
        protected override void OnDisconnecting()
        {
            // Close the port if it's open
            if (_Port.IsOpen)
                _Port.Close();

            // And continue
            base.OnDisconnecting();
        }
#endif

        protected override bool DetectProtocol()
        {
            // If no connections are allowed, exit
            if (!AllowConnections)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, Name + " is excluded from testing."));
                return false;
            }

#if PocketPC
            // What kind is this?
            if (!Devices.AllowInfraredConnections && Name.IndexOf("Infrared") != -1)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, Name + " will not be tested because infrared devices are currently excluded."));
                return false;
            }
#endif
            // Is it a Bluetooth serial port?
            if (!Devices.AllowBluetoothConnections
                && Name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) != -1)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                   new DeviceDetectionException(this, Name + " will not be tested because Bluetooth devices are currently excluded."));
                return false;
            }

            // Is it Bluetooth but Bluetooth is turned off?
            if (Devices.AllowBluetoothConnections
                && Name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) != -1
                && !Devices.IsBluetoothEnabled)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                   new DeviceDetectionException(this, Name + " will not be tested because Bluetooth is turned off."));
                return false;
            }

            // Have we reached the maximum allowed failures?
            if (SuccessfulDetectionCount == 0 && FailedDetectionCount >= 100)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                   new DeviceDetectionException(this, Name + " will not be tested because it has failed detection over " + MaximumAllowedFailures.ToString(CultureInfo.CurrentCulture) + " times with no success."));
                return false;
            }

            /* The first step here is to detect the baud rate.  Since NMEA
             * devices transmit only ASCII characters, we can keep reading bytes
             * until a few ASCII characters are received.
             */

            try
            {
                // Open a connection
                Open();
            }
            catch (InvalidOperationException)
            {
                // According to MSDN docs, this means the port is already open!  So, continue.
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // One of the parameters of the port is incorrect.  Flag the error
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, Name + " could not be opened because of an invalid parameter.  GPS connections should be using at least a 4800,8,N,1,None connection to work properly.", ex));
                return false;
            }
            catch (ArgumentException ex)
            {
                // The port name doesn't begin with "COM" or the file type of the port is not supported.
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, Name + " does not appear to exist.", ex));
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Security may have denied the connection
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, "A security PIN entered for " + Name + " was incorrect.  The device should be re-paired in the Bluetooth Manager.", ex));
                return false;
            }
            catch (Exception ex)
            {
                Devices.OnDeviceDetectionAttemptFailed(
                    new DeviceDetectionException(this, ex));
                return false;
            }

            /* If we get here, the serial port is successfully opened. */

            /* We have a list of baud rates to test.  We can speed up this test, however, if
             * we know the "last successful baud rate," the baud rate of the port when it last 
             * sent NMEA data.  So, if we know this value, test it FIRST to dramatically improve 
             * speed.
             */

            List<int> _BaudRatesToTest = new List<int>(_DetectionBaudRates);

            // Do we have the last known rate?
            if (_LastSuccessfulBaudRate != 0)
            {
                // Remove it from the list
                _BaudRatesToTest.Remove(_LastSuccessfulBaudRate);
                // And insert it at the beginning
                _BaudRatesToTest.Insert(0, _LastSuccessfulBaudRate);
            }

#if PocketPC
            // Get the stream we're working with
            SerialStream serialStream = (SerialStream)BaseStream;
#endif
            /* For the purposes of detection, we should be getting data immediately.
             * As a result, we can use agressive timeouts here.  Only a tiny handful of devices
             * need longer than two seconds to start transmitting data.
             */
#if PocketPC
            serialStream.ReadTimeout = 1000;
#else
            _Port.ReadTimeout = 1000;
#endif

            // Loop through all baud rates to test
            int baudCount = _BaudRatesToTest.Count;
            for (int index = 0; index < baudCount; index++)
            {
#if !PocketPC
                // Set the port baud rate
                _Port.BaudRate = _BaudRatesToTest[index];

                // Clear any old data
                _Port.DiscardInBuffer();
#else
                // Set the port baud rate
                serialStream.BaudRate = _BaudRatesToTest[index];

                // Clear any old data
                serialStream.DiscardInBuffer();
#endif
                // Read a new buffer
                byte[] buffer = new byte[NmeaReader.IdealNmeaBufferSize];
                int bytesRead = 0;

                try
                {
                    // Read the buffer in
                    bytesRead = BaseStream.Read(buffer, 0, NmeaReader.IdealNmeaBufferSize);
                }
#if PocketPC
                catch (IOException ex)
                {
                    /* The Samsung Omina is reporting an IOException.  Internally, the InnerException
                     * is a TimeoutException.
                     */

                    // No data was read whatsoever.  We'd at least get garbage if there was a device, right?
                    // Immediately return false.
                    Devices.OnDeviceDetectionAttemptFailed(
                        new DeviceDetectionException(this, Name + " returned no data after the timeout expired.", ex));
                    return false;
                }
#endif
                catch (InvalidOperationException ex)
                {
                    // The port is not open!  Probably closed?
                    try
                    {
                        // Try opening it again.
                        Open();
                    }
                    catch
                    {
                        // Failure.  Abort all testing.
                        Devices.OnDeviceDetectionAttemptFailed(
                            new DeviceDetectionException(this, Name + " could not be opened.", ex));
                        return false;
                    }
                }
                catch (TimeoutException)
                {
                    // No love at this baud rate.  Step down to a lower speed
                    continue;
                }

                // Analyze the buffer for contiguous ASCII data
                int contiguousASCIICharacters = 0;
                for (int count = 0; count < bytesRead; count++)
                {
                    // Get the byte
                    byte testByte = buffer[count];
                    if (testByte == 0)
                        continue;

                    // Is this byte outside of the range of ASCII characters?
                    if (testByte < 10 || testByte > 125)
                    {
                        // Yes.  Flag this as unlikely to be ASCII (or NMEA)
                        contiguousASCIICharacters = 0;
                    }
                    else
                    {
                        /* This is ASCII!  How many of these do we have in a row?
                         * If we're at the correct baud rate, we'll get a string of 'em.
                         * Sometimes, we'll get a blarp of non-ASCII followed by regular
                         * ASCII.  And yes, 'blarp' is now a word meaning "garbage data 
                         * which can be safely ignored".
                         */
                        contiguousASCIICharacters++;
                    }
                }

                // If we don't have a good set of ASCII, try another baud rate
                if (contiguousASCIICharacters < 10)
                    continue;

#if !PocketPC
                    // This is a good sign that we have NMEA.  Bump up our thread priority
                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
#endif

                /* We have ASCII.  Try up to 10 times to get a full sentences.
                 * NOTE: On the HTC P3300, an OutOfMemoryException occurs when using SerialPort.ReadLine().
                 *       However, a StreamReader.ReadLine() works just fine.  This suggests that SerialPort.ReadLine()
                 *       is buggy!  Use a StreamReader to get the job done.
                 */
                StreamReader reader = new StreamReader(BaseStream, ASCIIEncoding.ASCII, false, NmeaReader.IdealNmeaBufferSize);
                for (int count = 0; count < 10; count++)
                {
                    // Read a line
                    string testLine = null;
                    try
                    {
                        testLine = reader.ReadLine();
                    }
                    catch (TimeoutException ex)
                    {
                        Devices.OnDeviceDetectionAttemptFailed(
                            new DeviceDetectionException(this, Name + " did not respond to an attempt to read data.", ex));
                        return false;
                    }

                    if (
                        // Does it begin with a dollar sign?
                        testLine.StartsWith("$", StringComparison.OrdinalIgnoreCase)
                        // Is there an asterisk before that last two characters?
                        && testLine.IndexOf("*", StringComparison.OrdinalIgnoreCase) == testLine.Length - 3)
                    {
                        // Yes!  This is an NMEA device.

#if !PocketPC
                        // Set the "last successful" baud rate
                        _LastSuccessfulBaudRate = _Port.BaudRate;
#else
                        // Set the "last successful" baud rate
                        _LastSuccessfulBaudRate = serialStream.BaudRate;

                        // Update the baud rate of the device
                        _BaudRate = _LastSuccessfulBaudRate;
#endif

                        return true;
                    }
                }
            }

            /* The device did not return any detectable data. */
            Devices.OnDeviceDetectionAttemptFailed(
                new DeviceDetectionException(this, Name + " was tested at multiple baud rates but no GPS data was found."));
            return false;
        }

#if !PocketPC
        public override void CancelDetection()
        {
            /* Hooray .NET!  Aborting a thread can cause an ObjectDisposedException in the SerialPort's internal code. 
             * As a result, we can't just forcefully abort the thread.  Close the port.
             */

            if(IsDetectionInProgress && _Port != null && _Port.IsOpen)
                _Port.Close();

            // Continue to abort the thread
            base.CancelDetection();
        }
#endif

#if !PocketPC
        protected override void Dispose(bool disposing)
        {
            if (_Port != null)
            {
                if (_Port.IsOpen)
                {
                    try { _Port.BaseStream.Close(); } catch { }
#if Framework30
                    try { _Port.BaseStream.Dispose(); } catch { }
#endif
                    try { _Port.Close(); } catch { }
                }
                try { _Port.Dispose(); } catch { }
            }

            if (disposing)
            {                
                _Port = null;
            }

            base.Dispose(disposing);
        }
#endif


        protected override void OnCacheWrite()
        {
#if !PocketPC
            // In rare cases I get a NullReferenceException for the _Port
            if (_Port == null)
                return;
#endif

            // Save device stats
            RegistryKey deviceKey = null;
            try
            {
#if !PocketPC
                deviceKey = Registry.LocalMachine.CreateSubKey(RootKeyName + _Port.PortName);
#else
                deviceKey = Registry.LocalMachine.CreateSubKey(RootKeyName + _Port);
#endif

                if (deviceKey != null)
                {
                    // Set the friendly name
                    if (!String.IsNullOrEmpty(_Name))
                        deviceKey.SetValue(DefaultRegistryValueName, _Name);

                    // Update the baud rate and etc.
#if !PocketPC
                    deviceKey.SetValue("Baud Rate", _Port.BaudRate);
#else
                    deviceKey.SetValue("Baud Rate", _BaudRate);
#endif

                    // Update the success/fail statistics
                    deviceKey.SetValue("Number of Times Detected", SuccessfulDetectionCount);
                    deviceKey.SetValue("Number of Times Failed", FailedDetectionCount);
                    deviceKey.SetValue("Date Last Detected", DateDetected.ToString("G", CultureInfo.InvariantCulture));
                    deviceKey.SetValue("Date Last Connected", DateConnected.ToString("G", CultureInfo.InvariantCulture));

                    // Remember the baud rate if it's not zero
                    if (_LastSuccessfulBaudRate != 0)
                        deviceKey.SetValue("Last Successful Baud Rate", _LastSuccessfulBaudRate);
                }
            }
            catch (UnauthorizedAccessException)
            { }
            finally
            {
                if (deviceKey != null)
                    deviceKey.Close();
            }
        }

        protected override void OnCacheRemove()
        {
            //Delete the entire key for this port
            Registry.LocalMachine.DeleteSubKeyTree(RootKeyName + 
#if !PocketPC
                _Port.PortName
#else
                _Port
#endif
            );
        }

        protected override void OnCacheRead()
        {
            // Save device stats
#if !PocketPC
            RegistryKey deviceKey = Registry.LocalMachine.OpenSubKey(RootKeyName + _Port.PortName, false);
#else
            RegistryKey deviceKey = Registry.LocalMachine.OpenSubKey(RootKeyName + _Port, false);
#endif
            if (deviceKey == null)
                return;

            // Update the baud rate and etc.
            foreach (string name in deviceKey.GetValueNames())
            {
                switch (name)
                {
                    case DefaultRegistryValueName:
                        _Name = Convert.ToString(deviceKey.GetValue(name));
                        break;
                    case "Baud Rate":
#if !PocketPC
                        _Port.BaudRate = Convert.ToInt32(deviceKey.GetValue(name), CultureInfo.InvariantCulture);
#else
                        _BaudRate = Convert.ToInt32(deviceKey.GetValue(name));
#endif
                        break;
                    case "Number of Times Detected":
                        SetSuccessfulDetectionCount(Convert.ToInt32(deviceKey.GetValue(name), CultureInfo.InvariantCulture));
                        break;
                    case "Number of Times Failed":
                        SetFailedDetectionCount(Convert.ToInt32(deviceKey.GetValue(name), CultureInfo.InvariantCulture));
                        break;
                    case "Date Last Detected":
                        SetDateDetected(Convert.ToDateTime(deviceKey.GetValue(name), CultureInfo.InvariantCulture));
                        break;
                    case "Date Last Connected":
                        SetDateConnected(Convert.ToDateTime(deviceKey.GetValue(name), CultureInfo.InvariantCulture));
                        break;
                    case "Last Successful Baud Rate":
                        _LastSuccessfulBaudRate = Convert.ToInt32(deviceKey.GetValue(name), CultureInfo.InvariantCulture);
                        break;
                }
            }
        }

        #endregion

        #region Static Properties

        /// <summary>
        /// Controls the baud rates tested during GPS protocol detection.
        /// </summary>
        /// <remarks><para>A GPS device may support any number of baud rates.  As a result, the GPS.NET device detection system
        /// needs to test multiple baud rates in order to find a match.  This collection controls the list of baud rates
        /// tested during detection.</para>
        /// <para>Advanced GPS developers can improve performance by trimming this list down to baud rates which will actually 
        /// be used.</para>
        /// </remarks>
        public static IList<int> DetectionBaudRates
        {
            get
            {
                return _DetectionBaudRates;
            }
        }

        /// <summary>
        /// Controls the maximum allowed detection failures before a device is excluded from detection.
        /// </summary>
        /// <remarks><para>Some devices involved with device detection are not GPS devices.  For example, a serial device search
        /// could return a bar code scanner, an infrared port, or the neighbor's computer.  This property controls how many times a device will be
        /// tested before it is no longer included for searches.  If a device's failure count goes beyond this number, attempts
        /// will no longer be made to connect to the device.</para></remarks>
        public static int MaximumAllowedFailures
        {
            get
            {
                return _MaximumAllowedFailures;
            }
            set
            {
                _MaximumAllowedFailures = value;
            }
        }

        public static void ClearCache()
        {
            // Remove the entire branch of cached devices

        }

        /// <summary>
        /// Returns a list of known Bluetooth devices.
        /// </summary>
        /// <remarks><para>To maximize performance, GPS.NET will record information about each device it encounters for the purposes of organizing
        /// and speeding up the GPS device detection process.  This property returns a list of all known serial devices.</para>
        /// <para>Since this list is managed automatically, it should not be modified.</para>
        /// </remarks>
        public static IList<SerialDevice> Cache
        {
            get
            {
                List<SerialDevice> devices = new List<SerialDevice>();

                // Add virtual ports for Bluetooth devices
                IList<BluetoothDevice> cache = BluetoothDevice.Cache;
                for (int index = 0; index < cache.Count; index++)
                {
                    // Get the device
                    BluetoothDevice device = cache[index];

                    // Does this Bluetooth device have a serial port?
                    SerialDevice virtualDevice = device.VirtualSerialPort;
                    if (virtualDevice != null)
                        if (!devices.Contains(virtualDevice))
                            devices.Add(virtualDevice);
                }

#if !PocketPC

                // Open HKLM\HARDWARE\DEVICEMAP\SERIALCOMM
                RegistryKey portsKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM", false);
                if (portsKey != null)
                {
                    // Get a list of keys underneath this one
                    string[] portKeys = portsKey.GetValueNames();

                    #region Pass #1: Analysis of actual ports

                    int count = portKeys.Length;
                    for (int index = 0; index < count; index++)
                    {
                        // Get the name of the registry key for this device
                        string portKey = portKeys[index];

                        // Is this device a Bluetooth virtual serial port?
                        if (portKey.Contains(@"\BthModem"))
                        {
                            /* Yes.  We already have a way to associate BluetoothDevice objects
                             * with SerialDevice objects, so just skip this.
                             */
                            continue;
                        }

                        /* A bug in the Microsoft Bluetooth stack causes the registry value to have an extra 
                         * byte, which mucks shiz up.  So, in order to scrub those out, we must use ASCII encoding
                         * to convert the string to bytes and back.  This causes any garbage chars to become "?"
                         * which is never valid for a COM: port name.
                         */

                        string portName = (string)portsKey.GetValue(portKey);

                        // Convert the ASCII bytes to a string
                        portName =
                            ASCIIEncoding.ASCII.GetString(
                            // Convert the registry value to ASCII bytes
                            ASCIIEncoding.ASCII.GetBytes(portName))
                            // Lastly, remove "?" characters
                            .Replace("?", string.Empty);

                        // Lastly, append a colon
                        portName = portName + ":";

                        // Make a device with the port name
                        SerialDevice device = new SerialDevice(portName);

                        // Add it to the results
                        devices.Add(device);
                    }

                    // Finally, clean up
                    portsKey.Close();

                    #endregion
                }
#else
                #region WIDCOMM Bluetooth via Serial

                // Look for automatic connections
                RegistryKey devicesKey = Registry.LocalMachine.OpenSubKey(@"Software\WIDCOMM\BtConfig\AutoConnect", false);
                if (devicesKey != null)
                {
                    // Get the subkey
                    string[] subKeys = devicesKey.GetSubKeyNames();
                    foreach (string key in subKeys)
                    {
                        RegistryKey autoConnectKey = devicesKey.OpenSubKey(key, false);

                        // Look for values
                        string devicePort = "COM" + Convert.ToInt32(key).ToString() + ":";
                        byte[] bytes = (byte[])autoConnectKey.GetValue("BDName");
                        string friendlyName = System.Text.ASCIIEncoding.ASCII.GetString(bytes, 0, bytes.Length).Replace("\0", string.Empty);

                        // Make a new device.  Since it's Bluetooth, we can use a high baud rate (really ANY baud rate)
                        SerialDevice newDevice = new SerialDevice(devicePort, 115200);

                        // Add it if it's not already there
                        if (!devices.Contains(newDevice))
                        {
                            // Set it's in-English name
                            newDevice.SetName(friendlyName);

                            // Append it
                            devices.Add(newDevice);
                        }

                        //// Does it already exist?
                        //exists = false;
                        //for (int index2 = 0; index2 < devices.Count; index2++)
                        //{
                        //    // Look for a serial device with the same port (COM2: etc.)
                        //    SerialDevice existing = devices[index2];
                        //    if (existing.Port.Equals(devicePort))
                        //    {
                        //        exists = true;
                        //        break;
                        //    }
                        //}

                        //// Does it already exist?  If not, add it
                        //if (!exists)
                        //{


                        //    // Append to the collection
                        //    devices.Add(newDevice);
                        //}

                        autoConnectKey.Close();
                    }
                    devicesKey.Close();
                }

                // Look for a virtual serial port for Bluetooth devices                
                devicesKey = Registry.LocalMachine.OpenSubKey(@"Software\WIDCOMM\Connections", false);
                if (devicesKey != null)
                {
                    // Each subkey is a device
                    string[] deviceKeys = devicesKey.GetSubKeyNames();
                    for (int index = 0; index < deviceKeys.Length; index++)
                    {
                        // Open the sub-key
                        RegistryKey virtualSerialDeviceKey = devicesKey.OpenSubKey(deviceKeys[index], false);
                        if (virtualSerialDeviceKey != null)
                        {
                            // Now... get the name and COM port number
                            string friendlyName = Convert.ToString(virtualSerialDeviceKey.GetValue("BDName"));
                            string devicePort = "COM" + Convert.ToString(virtualSerialDeviceKey.GetValue("ComPortNumber")) + ":";

                            // Make a new device.  Since it's Bluetooth, we can use a high baud rate (really ANY baud rate)
                            SerialDevice newDevice = new SerialDevice(devicePort, 115200);

                            // Add it if it's not already there
                            if (!devices.Contains(newDevice))
                            {
                                // Set it's in-English name
                                newDevice.SetName(friendlyName);

                                // Append it
                                devices.Add(newDevice);
                            }


                            //// Does it already exist?
                            //exists = false;
                            //for (int index2 = 0; index2 < devices.Count; index2++)
                            //{
                            //    // Look for a serial device with the same port (COM2: etc.)
                            //    SerialDevice existing = devices[index2];
                            //    if (existing.Port.Equals(devicePort))
                            //    {
                            //        exists = true;
                            //        break;
                            //    }
                            //}

                            //// Does it already exist?  If not, add it
                            //if (!exists)
                            //{
                            //    // Make a new device.  Since it's Bluetooth, we can use a high baud rate (really ANY baud rate)
                            //    SerialDevice newDevice = new SerialDevice(devicePort, 115200);

                            //    // Set it's in-English name
                            //    newDevice.SetName(friendlyName);

                            //    // Append to the collection
                            //    devices.Add(newDevice);
                            //}

                            virtualSerialDeviceKey.Close();
                        }
                    }
                    devicesKey.Close();
                }


                #endregion

                #region Analyze HKLM\Drivers\BuiltIn

                // Look in the list of active devices for the port
                devicesKey = Registry.LocalMachine.OpenSubKey(@"Drivers\BuiltIn", false);
                if (devicesKey != null)
                {
                    // Get all the drivers
                    string[] subKeys = devicesKey.GetSubKeyNames();

                    // Now analyze each one for a match
                    for (int index = 0; index < subKeys.Length; index++)
                    {
                        RegistryKey subKey = null;
                        try
                        {
                            subKey = devicesKey.OpenSubKey(subKeys[index], false);
                            if (subKey != null)
                            {
                                // Now, look for a name value match
                                string devicePort = Convert.ToString(subKey.GetValue("Prefix"))
                                    + Convert.ToString(subKey.GetValue("Index")) + ":";

                                // If it doesn't start with "COM" then skip it
                                if (!devicePort.StartsWith("COM", StringComparison.InvariantCultureIgnoreCase))
                                    continue;

                                // Skip the GPS Intermediate Driver public port
                                if (GpsIntermediateDriver.IsSupported
                                    && GpsIntermediateDriver.Current.Port.Equals(devicePort))
                                    continue;

                                // Find a friendly name
                                string friendlyName = Convert.ToString(subKey.GetValue("FriendlyName"));

                                // Is it an array?
                                if (friendlyName.IndexOf("[]") != -1)
                                    friendlyName = ((string[])subKey.GetValue("FriendlyName"))[0];

                                if (string.IsNullOrEmpty(friendlyName))
                                {
                                    // Try harder to get a friendly name
                                    string dll = Convert.ToString(subKey.GetValue("Dll"));
                                    if (dll.Equals("IRCOMM.DLL", StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        friendlyName = "Infrared Port on " + devicePort;
                                    }
                                    else if (dll.Equals("RILGSM.dll", StringComparison.InvariantCultureIgnoreCase))
                                    {
                                        /* I have no idea what the fuck an RIL driver is, but it doesn't have anything to do with GPS.
                                         * Some COM ports actually cause problems if you open them.  So, this port will be excluded.
                                         */
                                        continue;
                                    }
                                    else
                                    {
                                        friendlyName = devicePort;
                                    }
                                }


                                // Make a new device.  Since it's Bluetooth, we can use a high baud rate (really ANY baud rate)
                                SerialDevice newDevice = new SerialDevice(devicePort);

                                // Add it if it's not already there
                                if (!devices.Contains(newDevice))
                                {
                                    // Set it's in-English name
                                    newDevice.SetName(friendlyName);

                                    // Append it
                                    devices.Add(newDevice);
                                }


                                //// Add the port
                                //exists = false;
                                //for (int index2 = 0; index2 < devices.Count; index2++)
                                //{
                                //    // Look for a serial device with the same port (COM2: etc.)
                                //    SerialDevice existing = devices[index2];
                                //    if (existing.Port.Equals(devicePort))
                                //    {
                                //        exists = true;
                                //        break;
                                //    }
                                //}

                                //// Does it already exist?  If not, add it
                                //if (!exists)
                                //{
                                //    // Make a new device
                                //    SerialDevice newDevice = new SerialDevice(devicePort);

                                //    // Set it's in-English name
                                //    newDevice.SetName(friendlyName);

                                //    // Append to the collection
                                //    devices.Add(newDevice);
                                //}
                            }
                        }
                        catch { }
                        finally
                        {
                            if (subKey != null)
                                subKey.Close();
                        }
                    }

                    // Finally, close the key
                    devicesKey.Close();
                }

                #endregion

                #region Examine HKLM\Drivers\Active

                // Look in the list of active devices for the port
                devicesKey = Registry.LocalMachine.OpenSubKey(@"Drivers\Active", false);
                if (devicesKey != null)
                {
                    // Get all the drivers
                    string[] subKeys = devicesKey.GetSubKeyNames();

                    // Now analyze each one for a match
                    for (int index = 0; index < subKeys.Length; index++)
                    {
                        RegistryKey subKey = null;
                        try
                        {
                            subKey = devicesKey.OpenSubKey(subKeys[index], false);
                            if (subKey != null)
                            {
                                // Now, look for a name value match
                                string devicePort = Convert.ToString(subKey.GetValue("Name"));

                                // If it doesn't start with "COM" then skip it
                                if (!devicePort.StartsWith("COM", StringComparison.InvariantCultureIgnoreCase))
                                    continue;

                                // Skip the GPS Intermediate Driver public port
                                if (GpsIntermediateDriver.IsSupported
                                    && GpsIntermediateDriver.Current.Port.Equals(devicePort))
                                    continue;

                                // Find a friendly name
                                string friendlyName = Convert.ToString(subKey.GetValue("Key"));
                                if (friendlyName.IndexOf("PCMCIA") != -1)
                                    friendlyName = "CompactFlash® Device on " + devicePort;
                                else if (friendlyName.IndexOf("USB") != -1)
                                    friendlyName = "USB Device on " + devicePort;
                                else if (friendlyName.IndexOf("Bluetooth") != -1)
                                    friendlyName = "Bluetooth Virtual Serial Port on " + devicePort;
                                else if (friendlyName.IndexOf("VirtCOM") != -1)
                                {
                                    /* This is the Radio Interface Layer (http://msdn.microsoft.com/en-us/library/ms890075.aspx), and
                                     * has nothing to do with GPS.
                                     */
                                    continue;
                                }
                                else if (friendlyName.IndexOf("IrCOMM") != -1 || friendlyName.IndexOf("IrDA") != -1)
                                    friendlyName = "Infrared Port on " + devicePort;
                                else
                                    friendlyName = devicePort;

                                // Is this an expansion slot?
                                if (subKeys[index].Equals("ExpSlot", StringComparison.InvariantCultureIgnoreCase))
                                    friendlyName += " (Expansion Slot)";

                                // Make a new device.  Since it's Bluetooth, we can use a high baud rate (really ANY baud rate)
                                SerialDevice newDevice = new SerialDevice(devicePort);

                                // Add it if it's not already there
                                if (!devices.Contains(newDevice))
                                {
                                    // Set it's in-English name
                                    newDevice.SetName(friendlyName);

                                    // Append it
                                    devices.Add(newDevice);
                                }

                                //// Add the port
                                //exists = false;
                                //for (int index2 = 0; index2 < devices.Count; index2++)
                                //{
                                //    // Look for a serial device with the same port (COM2: etc.)
                                //    SerialDevice existing = devices[index2];
                                //    if (existing.Port.Equals(devicePort))
                                //    {
                                //        exists = true;
                                //        break;
                                //    }
                                //}

                                //// Does it already exist?  If not, add it
                                //if (!exists)
                                //{
                                //    SerialDevice newDevice = new SerialDevice(devicePort);
                                //    newDevice.SetName(friendlyName);
                                //    devices.Add(newDevice);
                                //}
                            }
                        }
                        catch { }
                        finally
                        {
                            if (subKey != null)
                                subKey.Close();
                        }
                    }

                    // Finally, close the key
                    devicesKey.Close();
                }


                #endregion
#endif

                /* Sort the list based on the most reliable device first.
                 * Device detection will execute in this order.
                 */
                devices.Sort(Device.BestDeviceComparer);

                // Return the results
                return devices;
            }
        }

        #endregion

        #region Internal Methods

        internal void SetName(string name)
        {
            _Name = name;
        }

        #endregion

        #region IEquatable<SerialDevice> Members

        public bool Equals(SerialDevice other)
        {
            if (object.ReferenceEquals(other, null))
                return false;
            return Port.Equals(other.Port, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}