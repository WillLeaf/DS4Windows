﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ComponentModel;

namespace DS4Windows
{
    public enum DsState : byte
    {
        [Description("Disconnected")]
        Disconnected = 0x00,
        [Description("Reserved")]
        Reserved = 0x01,
        [Description("Connected")]
        Connected = 0x02
    };

    public enum DsConnection : byte
    {
        [Description("None")]
        None = 0x00,
        [Description("Usb")]
        Usb = 0x01,
        [Description("Bluetooth")]
        Bluetooth = 0x02
    };

    public enum DsModel : byte
    {
        [Description("None")]
        None = 0,
        [Description("DualShock 3")]
        DS3 = 1,
        [Description("DualShock 4")]
        DS4 = 2,
        [Description("Generic Gamepad")]
        Generic = 3
    }

    public enum DsBattery : byte
    {
        Charging = 0x80,
        Charged = 0xEF
    };

    public struct DualShockPadMeta
    {
        public byte PadId;
        public DsState PadState;
        public DsConnection ConnectionType;
        public DsModel Model;
        public PhysicalAddress PadMacAddress;
        public byte BatteryStatus;
        public bool IsActive;
    }

    class UdpServer
	{
		private Socket udpSock;
		private uint serverId;
		private bool running;
		private byte[] recvBuffer = new byte[1024];

        public delegate DualShockPadMeta GetPadDetail(int padIdx);

		private GetPadDetail portInfoGet;

		public UdpServer(GetPadDetail getPadDetailDel)
		{
			portInfoGet = getPadDetailDel;
		}

		enum MessageType
		{
			DSUC_VersionReq = 0x100000,
			DSUS_VersionRsp = 0x100000,
			DSUC_ListPorts	= 0x100001,
			DSUS_PortInfo	= 0x100001,
			DSUC_PadDataReq = 0x100002,
			DSUS_PadDataRsp = 0x100002,
		};

		private const ushort MaxProtocolVersion = 1001;

		class ClientRequestTimes
		{
			DateTime allPads;
			DateTime[] padIds;
			Dictionary<PhysicalAddress, DateTime> padMacs;

			public DateTime AllPadsTime { get { return allPads; } }
			public DateTime[] PadIdsTime { get { return padIds; } }
			public Dictionary<PhysicalAddress, DateTime> PadMacsTime { get { return padMacs; } }

			public ClientRequestTimes()
			{
				allPads = DateTime.MinValue;
				padIds = new DateTime[4];

				for (int i = 0; i < padIds.Length; i++)
					padIds[i] = DateTime.MinValue;

				padMacs = new Dictionary<PhysicalAddress,DateTime>();
			}

			public void RequestPadInfo(byte regFlags, byte idToReg, PhysicalAddress macToReg)
			{
				if (regFlags == 0)
					allPads = DateTime.UtcNow;
				else
				{
					if ((regFlags & 0x01) != 0) //id valid
					{
						if (idToReg < padIds.Length)
							padIds[idToReg] = DateTime.UtcNow;
					}
					if ((regFlags & 0x02) != 0) //mac valid
					{
						padMacs[macToReg] = DateTime.UtcNow;
					}
				}
			}
		}

		private Dictionary<IPEndPoint, ClientRequestTimes> clients = new Dictionary<IPEndPoint, ClientRequestTimes>();

		private void SendPacket(IPEndPoint clientEP, byte[] usefulData, ushort reqProtocolVersion = MaxProtocolVersion)
		{
			byte[] packetData = new byte[usefulData.Length + 16];
			Array.Copy(usefulData, 0, packetData, 16, usefulData.Length);

			int currIdx = 0;
			packetData[currIdx++] = (byte)'D';
			packetData[currIdx++] = (byte)'S';
			packetData[currIdx++] = (byte)'U';
			packetData[currIdx++] = (byte)'S';

			Array.Copy(BitConverter.GetBytes((ushort)reqProtocolVersion), 0, packetData, currIdx, 2);
			currIdx += 2;

			Array.Copy(BitConverter.GetBytes((ushort)usefulData.Length), 0, packetData, currIdx, 2);
			currIdx += 2;

			int crcPos = currIdx;
			packetData[currIdx++] = 0;
			packetData[currIdx++] = 0;
			packetData[currIdx++] = 0;
			packetData[currIdx++] = 0;

			Array.Copy(BitConverter.GetBytes((uint)serverId), 0, packetData, currIdx, 4);
			currIdx += 4;

			uint crcCalc = Crc32.Compute(packetData);
			Array.Copy(BitConverter.GetBytes((uint)crcCalc), 0, packetData, crcPos, 4);

			try { udpSock.SendTo(packetData, clientEP);	}
			catch (Exception e) { }
		}

		private void ProcessIncoming(byte[] localMsg, IPEndPoint clientEP)
		{
			try
			{
				int currIdx = 0;
				if (localMsg[0] != 'D' || localMsg[1] != 'S' || localMsg[2] != 'U' || localMsg[3] != 'C')
					return;
				else
					currIdx += 4;

				uint protocolVer = BitConverter.ToUInt16(localMsg, currIdx);
				currIdx += 2;

				if (protocolVer > MaxProtocolVersion)
					return;

				uint packetSize = BitConverter.ToUInt16(localMsg, currIdx);
				currIdx += 2;

				if (packetSize < 0)
					return;

				packetSize += 16; //size of header
				if (packetSize > localMsg.Length)
					return;
				else if (packetSize < localMsg.Length)
				{
					byte[] newMsg = new byte[packetSize];
					Array.Copy(localMsg, newMsg, packetSize);
					localMsg = newMsg;
				}

				uint crcValue = BitConverter.ToUInt32(localMsg, currIdx);
				//zero out the crc32 in the packet once we got it since that's whats needed for calculation
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;
				localMsg[currIdx++] = 0;

				uint crcCalc = Crc32.Compute(localMsg);
				if (crcValue != crcCalc)
					return;

				uint clientId = BitConverter.ToUInt32(localMsg, currIdx);
				currIdx += 4;

				uint messageType = BitConverter.ToUInt32(localMsg, currIdx);
				currIdx += 4;

				if (messageType == (uint)MessageType.DSUC_VersionReq)
				{
					byte[] outputData = new byte[8];
					int outIdx = 0;
					Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_VersionRsp), 0, outputData, outIdx, 4);
					outIdx += 4;
					Array.Copy(BitConverter.GetBytes((ushort)MaxProtocolVersion), 0, outputData, outIdx, 2);
					outIdx += 2;
					outputData[outIdx++] = 0;
					outputData[outIdx++] = 0;

					SendPacket(clientEP, outputData, 1001);
				}
				else if (messageType == (uint)MessageType.DSUC_ListPorts)
				{
					int numPadRequests = BitConverter.ToInt32(localMsg, currIdx);
					currIdx += 4;
					if (numPadRequests < 0 || numPadRequests > 4)
						return;

					int requestsIdx = currIdx;
					for (int i = 0; i < numPadRequests; i++)
					{
						byte currRequest = localMsg[requestsIdx+i];
						if (currRequest >= 4)
							return;
					}

					byte[] outputData = new byte[16];
					for (byte i = 0; i < numPadRequests; i++)
					{
						byte currRequest = localMsg[requestsIdx + i];
						var padData = portInfoGet(currRequest);

						int outIdx = 0;
						Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_PortInfo), 0, outputData, outIdx, 4);
						outIdx += 4;

						outputData[outIdx++] = (byte)padData.PadId;
						outputData[outIdx++] = (byte)padData.PadState;
						outputData[outIdx++] = (byte)padData.Model;
						outputData[outIdx++] = (byte)padData.ConnectionType;

                        byte[] addressBytes = null;
                        if (padData.PadMacAddress != null)
                            addressBytes = padData.PadMacAddress.GetAddressBytes();

						if (addressBytes != null && addressBytes.Length == 6)
						{
							outputData[outIdx++] = addressBytes[0];
							outputData[outIdx++] = addressBytes[1];
							outputData[outIdx++] = addressBytes[2];
							outputData[outIdx++] = addressBytes[3];
							outputData[outIdx++] = addressBytes[4];
							outputData[outIdx++] = addressBytes[5];
						}
						else
						{
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
							outputData[outIdx++] = 0;
						}

						outputData[outIdx++] = (byte)padData.BatteryStatus;
						outputData[outIdx++] = 0;

						SendPacket(clientEP, outputData, 1001);
					}
				}
				else if (messageType == (uint)MessageType.DSUC_PadDataReq)
				{
					byte regFlags = localMsg[currIdx++];
					byte idToReg = localMsg[currIdx++];
					PhysicalAddress macToReg = null;
					{
						byte[] macBytes = new byte[6];
						Array.Copy(localMsg, currIdx, macBytes, 0, macBytes.Length);
						currIdx += macBytes.Length;
						macToReg = new PhysicalAddress(macBytes);
					}

					lock(clients)
					{
						if (clients.ContainsKey(clientEP))
							clients[clientEP].RequestPadInfo(regFlags, idToReg, macToReg);
						else
						{
							var clientTimes = new ClientRequestTimes();
							clientTimes.RequestPadInfo(regFlags, idToReg, macToReg);
							clients[clientEP] = clientTimes;
						}
					}
				}
			}
			catch (Exception e) { }
		}

		private void ReceiveCallback(IAsyncResult iar)
		{
			byte[] localMsg = null;
			EndPoint clientEP = new IPEndPoint(IPAddress.Any, 0);

			try
			{
				//Get the received message.
				Socket recvSock = (Socket)iar.AsyncState;
				int msgLen = recvSock.EndReceiveFrom(iar, ref clientEP);

				localMsg = new byte[msgLen];
				Array.Copy(recvBuffer, localMsg, msgLen);
			}
			catch (Exception e) { }

			//Start another receive as soon as we copied the data
			StartReceive();

			//Process the data if its valid
			if (localMsg != null)
				ProcessIncoming(localMsg, (IPEndPoint)clientEP);
		}
		private void StartReceive()
		{
			try
			{
				if (running)
				{
					//Start listening for a new message.
					EndPoint newClientEP = new IPEndPoint(IPAddress.Any, 0);
					udpSock.BeginReceiveFrom(recvBuffer, 0, recvBuffer.Length, SocketFlags.None, ref newClientEP, ReceiveCallback, udpSock);
				}			
			}
			catch (SocketException ex) 
			{
				uint IOC_IN = 0x80000000;
				uint IOC_VENDOR = 0x18000000;
				uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
				udpSock.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);

				StartReceive(); 
			}
		}

		public void Start(int port)
		{
			if (running)
			{
				if (udpSock != null)
				{
					udpSock.Close();
					udpSock = null;
				}				
				running = false;
			}

			udpSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
			try { udpSock.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
			catch (SocketException ex) 
			{
				udpSock.Close();
				udpSock = null;

				throw ex;
			}

			byte[] randomBuf = new byte[4];
			new Random().NextBytes(randomBuf);
			serverId = BitConverter.ToUInt32(randomBuf,0);

			running = true;
			StartReceive();
		}

		public void Stop()
		{
			running = false;
			if (udpSock != null)
			{
				udpSock.Close();
				udpSock = null;
			}
		}

		private bool ReportToBuffer(DS4State hidReport, byte[] outputData, ref int outIdx)
		{
            outputData[outIdx] = 0;

            if (hidReport.DpadLeft) outputData[outIdx] |= 0x80;
            if (hidReport.DpadDown) outputData[outIdx] |= 0x40;
            if (hidReport.DpadRight) outputData[outIdx] |= 0x20;
            if (hidReport.DpadUp) outputData[outIdx] |= 0x10;

            if (hidReport.Options) outputData[outIdx] |= 0x08;
            if (hidReport.R3) outputData[outIdx] |= 0x04;
            if (hidReport.L3) outputData[outIdx] |= 0x02;
            if (hidReport.Share) outputData[outIdx] |= 0x01;

            outputData[++outIdx] = 0;

            if (hidReport.Square) outputData[outIdx] |= 0x80;
            if (hidReport.Cross) outputData[outIdx] |= 0x40;
            if (hidReport.Circle) outputData[outIdx] |= 0x20;
            if (hidReport.Triangle) outputData[outIdx] |= 0x10;

            if (hidReport.R1) outputData[outIdx] |= 0x08;
            if (hidReport.L1) outputData[outIdx] |= 0x04;
            if (hidReport.R2Btn) outputData[outIdx] |= 0x02;
            if (hidReport.L2Btn) outputData[outIdx] |= 0x01;

            outputData[++outIdx] = (hidReport.PS) ? (byte)1 : (byte)0;
            outputData[++outIdx] = (hidReport.TouchButton) ? (byte)1 : (byte)0;

            //Left stick
            outputData[++outIdx] = hidReport.LX;
            outputData[++outIdx] = hidReport.LY;
            outputData[outIdx] = (byte)(255 - outputData[outIdx]); //invert Y by convention

            //Right stick
            outputData[++outIdx] = hidReport.RX;
            outputData[++outIdx] = hidReport.RY;
            outputData[outIdx] = (byte)(255 - outputData[outIdx]); //invert Y by convention

            //we don't have analog buttons on DS4 :(
            outputData[++outIdx] = hidReport.DpadLeft ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.DpadDown ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.DpadRight ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.DpadUp ? (byte)0xFF : (byte)0x00;

            outputData[++outIdx] = hidReport.Square ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.Cross ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.Circle ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.Triangle ? (byte)0xFF : (byte)0x00;

            outputData[++outIdx] = hidReport.R1 ? (byte)0xFF : (byte)0x00;
            outputData[++outIdx] = hidReport.L1 ? (byte)0xFF : (byte)0x00;

            outputData[++outIdx] = hidReport.R2;
            outputData[++outIdx] = hidReport.L2;

            outIdx++;

            //DS4 only: touchpad points
            for (int i = 0; i < 2; i++)
			{
				var tpad = hidReport.TrackPadTouch0;
				if (i > 0)
					tpad = hidReport.TrackPadTouch1;

                outputData[outIdx++] = tpad.IsActive ? (byte)1 : (byte)0;
                outputData[outIdx++] = (byte)tpad.Id;
                Array.Copy(BitConverter.GetBytes((ushort)tpad.X), 0, outputData, outIdx, 2);
                outIdx += 2;
                Array.Copy(BitConverter.GetBytes((ushort)tpad.Y), 0, outputData, outIdx, 2);
                outIdx += 2;
            }

            //motion timestamp
            if (hidReport.Motion != null)
                Array.Copy(BitConverter.GetBytes((ulong)hidReport.Motion.timestampUs), 0, outputData, outIdx, 8);
            else
                Array.Clear(outputData, outIdx, 8);

            outIdx += 8;

            //accelerometer
            if (hidReport.Motion != null)
            {
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.accelX), 0, outputData, outIdx, 4);
                outIdx += 4;
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.accelY), 0, outputData, outIdx, 4);
                outIdx += 4;
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.accelZ), 0, outputData, outIdx, 4);
                outIdx += 4;
            }
            else
            {
                Array.Clear(outputData, outIdx, 12);
                outIdx += 12;
            }

            //gyroscope
            if (hidReport.Motion != null)
            {
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.gyroPitch), 0, outputData, outIdx, 4);
                outIdx += 4;
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.gyroYaw), 0, outputData, outIdx, 4);
                outIdx += 4;
                Array.Copy(BitConverter.GetBytes((float)hidReport.Motion.gyroRoll), 0, outputData, outIdx, 4);
                outIdx += 4;
            }
            else
            {
                Array.Clear(outputData, outIdx, 12);
                outIdx += 12;
            }

            return true;
		}

		public void NewReportIncoming(DualShockPadMeta padMeta, DS4State hidReport)
		{
			if (!running)
				return;

			byte[] outputData = new byte[84];
			int outIdx = 0;
			Array.Copy(BitConverter.GetBytes((uint)MessageType.DSUS_PadDataRsp), 0, outputData, outIdx, 4);
			outIdx += 4;

			outputData[outIdx++] = (byte)padMeta.PadId;
			outputData[outIdx++] = (byte)padMeta.PadState;
			outputData[outIdx++] = (byte)padMeta.Model;
			outputData[outIdx++] = (byte)padMeta.ConnectionType;
			{
				byte[] padMac = padMeta.PadMacAddress.GetAddressBytes();
				outputData[outIdx++] = padMac[0];
				outputData[outIdx++] = padMac[1];
				outputData[outIdx++] = padMac[2];
				outputData[outIdx++] = padMac[3];
				outputData[outIdx++] = padMac[4];
				outputData[outIdx++] = padMac[5];
			}			
			outputData[outIdx++] = (byte)padMeta.BatteryStatus;
			outputData[outIdx++] = padMeta.IsActive ? (byte)1 : (byte)0;

			Array.Copy(BitConverter.GetBytes((uint)hidReport.PacketCounter), 0, outputData, outIdx, 4);
			outIdx += 4;

			if (!ReportToBuffer(hidReport, outputData, ref outIdx))
				return;

			var clientsList = new List<IPEndPoint>();
			var now = DateTime.UtcNow;
			lock (clients)
			{
				var clientsToDelete = new List<IPEndPoint>();

				foreach (var cl in clients)
				{
					const double TimeoutLimit = 5;

					if ((now - cl.Value.AllPadsTime).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else if ((padMeta.PadId < cl.Value.PadIdsTime.Length) &&
							 (now - cl.Value.PadIdsTime[(byte)padMeta.PadId]).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else if (cl.Value.PadMacsTime.ContainsKey(padMeta.PadMacAddress) &&
							 (now - cl.Value.PadMacsTime[padMeta.PadMacAddress]).TotalSeconds < TimeoutLimit)
						clientsList.Add(cl.Key);
					else //check if this client is totally dead, and remove it if so
					{
						bool clientOk = false;
						for (int i=0; i<cl.Value.PadIdsTime.Length; i++)
						{
							var dur = (now - cl.Value.PadIdsTime[i]).TotalSeconds;
							if (dur < TimeoutLimit)
							{
								clientOk = true;
								break;
							}
						}
						if (!clientOk)
						{
							foreach (var dict in cl.Value.PadMacsTime)
							{
								var dur = (now - dict.Value).TotalSeconds;
								if (dur < TimeoutLimit)
								{
									clientOk = true;
									break;
								}
							}

							if (!clientOk)
								clientsToDelete.Add(cl.Key);
						}
					}
				}

				foreach (var delCl in clientsToDelete)
				{
					clients.Remove(delCl);
				}
				clientsToDelete.Clear();
				clientsToDelete = null;
			}

			foreach (var cl in clientsList)
			{
				try { SendPacket(cl, outputData, 1001); }
				catch (SocketException ex) { }
			}
			clientsList.Clear();
			clientsList = null;
		}
	}
}