﻿/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using World;
using World.Packets;
using Framework.Constants;
using Framework.Constants.World.V2_5_2_39570;
using Framework.Cryptography;
using Framework.IO;
using Framework.Networking;

using System;
using System.Net.Sockets;
using Framework.Logging;
using Framework.Realm;
using static World.Packets.AuthResponse;
using System.Collections.Generic;
using HermesProxy.World.Objects;

namespace World
{
    public class WorldSocket : SocketBase
    {
        static readonly string ClientConnectionInitialize = "WORLD OF WARCRAFT CONNECTION - CLIENT TO SERVER - V2";
        static readonly string ServerConnectionInitialize = "WORLD OF WARCRAFT CONNECTION - SERVER TO CLIENT - V2";

        static readonly byte[] AuthCheckSeed = { 0xC5, 0xC6, 0x98, 0x95, 0x76, 0x3F, 0x1D, 0xCD, 0xB6, 0xA1, 0x37, 0x28, 0xB3, 0x12, 0xFF, 0x8A };
        static readonly byte[] SessionKeySeed = { 0x58, 0xCB, 0xCF, 0x40, 0xFE, 0x2E, 0xCE, 0xA6, 0x5A, 0x90, 0xB8, 0x01, 0x68, 0x6C, 0x28, 0x0B };
        static readonly byte[] ContinuedSessionSeed = { 0x16, 0xAD, 0x0C, 0xD4, 0x46, 0xF9, 0x4F, 0xB2, 0xEF, 0x7D, 0xEA, 0x2A, 0x17, 0x66, 0x4D, 0x2F };
        static readonly byte[] EncryptionKeySeed = { 0xE9, 0x75, 0x3C, 0x50, 0x90, 0x93, 0x61, 0xDA, 0x3B, 0x07, 0xEE, 0xFA, 0xFF, 0x9D, 0x41, 0xB8 };

        static readonly int HeaderSize = 16;

        SocketBuffer _headerBuffer;
        SocketBuffer _packetBuffer;

        ConnectionType _connectType;
        ulong _key;

        byte[] _serverChallenge;
        WorldCrypt _worldCrypt;
        byte[] _sessionKey;
        byte[] _encryptKey;
        ConnectToKey _instanceConnectKey;
        RealmId _realmId;

        long _LastPingTime;

        ZLib.z_stream _compressionStream;

        public WorldSocket(Socket socket) : base(socket)
        {
            _connectType = ConnectionType.Realm;
            _serverChallenge = Array.Empty<byte>().GenerateRandomKey(16);
            _worldCrypt = new WorldCrypt();

            _encryptKey = new byte[16];

            _headerBuffer = new SocketBuffer(HeaderSize);
            _packetBuffer = new SocketBuffer(0);
        }

        public override void Dispose()
        {
            _serverChallenge = null;
            _sessionKey = null;
            _compressionStream = null;

            base.Dispose();
        }

        public override void Accept()
        {
            string ip_address = GetRemoteIpAddress().ToString();

            _packetBuffer.Resize(ClientConnectionInitialize.Length + 1);

            AsyncReadWithCallback(InitializeHandler);

            ByteBuffer packet = new();
            packet.WriteString(ServerConnectionInitialize);
            packet.WriteString("\n");
            AsyncWrite(packet.GetData());
        }

        void InitializeHandler(SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                CloseSocket();
                return;
            }

            if (args.BytesTransferred > 0)
            {
                if (_packetBuffer.GetRemainingSpace() > 0)
                {
                    // need to receive the header
                    int readHeaderSize = Math.Min(args.BytesTransferred, _packetBuffer.GetRemainingSpace());
                    _packetBuffer.Write(args.Buffer, 0, readHeaderSize);

                    if (_packetBuffer.GetRemainingSpace() > 0)
                    {
                        // Couldn't receive the whole header this time.
                        AsyncReadWithCallback(InitializeHandler);
                        return;
                    }

                    ByteBuffer buffer = new(_packetBuffer.GetData());
                    string initializer = buffer.ReadString((uint)ClientConnectionInitialize.Length);
                    if (initializer != ClientConnectionInitialize)
                    {
                        CloseSocket();
                        return;
                    }

                    byte terminator = buffer.ReadUInt8();
                    if (terminator != '\n')
                    {
                        CloseSocket();
                        return;
                    }

                    // Initialize the zlib stream
                    _compressionStream = new ZLib.z_stream();

                    // Initialize the deflate algo...
                    var z_res1 = ZLib.deflateInit2(_compressionStream, 1, 8, -15, 8, 0);
                    if (z_res1 != 0)
                    {
                        CloseSocket();
                        Log.Print(LogType.Error, $"Can't initialize packet compression (zlib: deflateInit2_) Error code: {z_res1}");
                        return;
                    }

                    _packetBuffer.Resize(0);
                    _packetBuffer.Reset();
                    HandleSendAuthSession();
                    AsyncRead();
                    return;
                }
            }
        }

        public override void ReadHandler(SocketAsyncEventArgs args)
        {
            if (!IsOpen())
                return;

            int currentReadIndex = 0;
            while (currentReadIndex < args.BytesTransferred)
            {
                if (_headerBuffer.GetRemainingSpace() > 0)
                {
                    // need to receive the header
                    int readHeaderSize = Math.Min(args.BytesTransferred - currentReadIndex, _headerBuffer.GetRemainingSpace());
                    _headerBuffer.Write(args.Buffer, currentReadIndex, readHeaderSize);
                    currentReadIndex += readHeaderSize;

                    if (_headerBuffer.GetRemainingSpace() > 0)
                        break; // Couldn't receive the whole header this time.

                    // We just received nice new header
                    if (!ReadHeader())
                    {
                        CloseSocket();
                        return;
                    }
                }

                // We have full read header, now check the data payload
                if (_packetBuffer.GetRemainingSpace() > 0)
                {
                    // need more data in the payload
                    int readDataSize = Math.Min(args.BytesTransferred - currentReadIndex, _packetBuffer.GetRemainingSpace());
                    _packetBuffer.Write(args.Buffer, currentReadIndex, readDataSize);
                    currentReadIndex += readDataSize;

                    if (_packetBuffer.GetRemainingSpace() > 0)
                        break; // Couldn't receive the whole data this time.
                }

                // just received fresh new payload
                ReadDataHandlerResult result = ReadData();
                _headerBuffer.Reset();
                if (result != ReadDataHandlerResult.Ok)
                {
                    if (result != ReadDataHandlerResult.WaitingForQuery)
                        CloseSocket();

                    return;
                }
            }

            AsyncRead();
        }

        bool ReadHeader()
        {
            PacketHeader header = new();
            header.Read(_headerBuffer.GetData());

            _packetBuffer.Resize(header.Size);
            return true;
        }

        ReadDataHandlerResult ReadData()
        {
            PacketHeader header = new();
            header.Read(_headerBuffer.GetData());

            if (!_worldCrypt.Decrypt(_packetBuffer.GetData(), header.Tag))
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadData(): client {GetRemoteIpAddress()} failed to decrypt packet (size: {header.Size})");
                return ReadDataHandlerResult.Error;
            }

            WorldPacket packet = new(_packetBuffer.GetData());
            _packetBuffer.Reset();

            Opcode opcode = (Opcode)packet.GetOpcode();

            Log.Print(LogType.Network, $"Received opcode {opcode.ToString()} ({(uint)opcode}).");

            if (opcode != Opcode.CMSG_HOTFIX_REQUEST && !header.IsValidSize())
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadHeaderHandler(): client {GetRemoteIpAddress()} sent malformed packet (size: {header.Size})");
                return ReadDataHandlerResult.Error;
            }

            switch (opcode)
            {
                case Opcode.CMSG_PING:
                    Ping ping = new(packet);
                    ping.Read();
                    if (!HandlePing(ping))
                        return ReadDataHandlerResult.Error;
                    break;
                case Opcode.CMSG_AUTH_SESSION:
                    AuthSession authSession = new(packet);
                    authSession.Read();
                    HandleAuthSession(authSession);
                    return ReadDataHandlerResult.WaitingForQuery;
                case Opcode.CMSG_AUTH_CONTINUED_SESSION:
                    AuthContinuedSession authContinuedSession = new(packet);
                    authContinuedSession.Read();
                    HandleAuthContinuedSession(authContinuedSession);
                    return ReadDataHandlerResult.WaitingForQuery;
                case Opcode.CMSG_KEEP_ALIVE:
                    break;
                case Opcode.CMSG_LOG_DISCONNECT:
                    break;
                case Opcode.CMSG_ENABLE_NAGLE:
                    SetNoDelay(false);
                    break;
                case Opcode.CMSG_CONNECT_TO_FAILED:
                    ConnectToFailed connectToFailed = new(packet);
                    connectToFailed.Read();
                    HandleConnectToFailed(connectToFailed);
                    break;
                case Opcode.CMSG_ENTER_ENCRYPTED_MODE_ACK:
                    HandleEnterEncryptedModeAck();
                    break;
                case Opcode.CMSG_ENUM_CHARACTERS:
                    SendCharEnum();
                    break;
                default:
                    HandlePacket(packet);
                    break;
            }

            return ReadDataHandlerResult.Ok;
        }

        public void HandlePacket(WorldPacket packet)
        {

        }

        public void SendPacket(ServerPacket packet)
        {
            if (!IsOpen())
                return;

            //packet.LogPacket();
            packet.WritePacketData();

            var data = packet.GetData();
            Opcode opcode = (Opcode)packet.GetOpcode();
            Log.Print(LogType.Network, $"Sending opcode {opcode.ToString()} ({(uint)opcode}).");

            ByteBuffer buffer = new();

            int packetSize = data.Length;
            if (packetSize > 0x400 && _worldCrypt.IsInitialized)
            {
                buffer.WriteInt32(packetSize + 2);
                buffer.WriteUInt32(ZLib.adler32(ZLib.adler32(0x9827D8F1, BitConverter.GetBytes((ushort)opcode), 2), data, (uint)packetSize));

                byte[] compressedData;
                uint compressedSize = CompressPacket(data, opcode, out compressedData);
                buffer.WriteUInt32(ZLib.adler32(0x9827D8F1, compressedData, compressedSize));
                buffer.WriteBytes(compressedData, compressedSize);

                packetSize = (int)(compressedSize + 12);
                opcode = Opcode.SMSG_COMPRESSED_PACKET;

                data = buffer.GetData();
            }

            buffer = new ByteBuffer();
            buffer.WriteUInt16((ushort)opcode);
            buffer.WriteBytes(data);
            packetSize += 2 /*opcode*/;

            data = buffer.GetData();

            PacketHeader header = new();
            header.Size = packetSize;
            _worldCrypt.Encrypt(ref data, ref header.Tag);

            ByteBuffer byteBuffer = new();
            header.Write(byteBuffer);
            byteBuffer.WriteBytes(data);

            AsyncWrite(byteBuffer.GetData());
        }

        public uint CompressPacket(byte[] data, Opcode opcode, out byte[] outData)
        {
            byte[] uncompressedData = BitConverter.GetBytes((ushort)opcode).Combine(data);

            uint bufferSize = ZLib.deflateBound(_compressionStream, (uint)data.Length);
            outData = new byte[bufferSize];

            _compressionStream.next_out = 0;
            _compressionStream.avail_out = bufferSize;
            _compressionStream.out_buf = outData;

            _compressionStream.next_in = 0;
            _compressionStream.avail_in = (uint)uncompressedData.Length;
            _compressionStream.in_buf = uncompressedData;

            int z_res = ZLib.deflate(_compressionStream, 2);
            if (z_res != 0)
            {
                Log.Print(LogType.Error, $"Can't compress packet data (zlib: deflate) Error code: {z_res} msg: {_compressionStream.msg}");
                return 0;
            }

            return bufferSize - _compressionStream.avail_out;
        }

        public override bool Update()
        {
            if (!base.Update())
                return false;

            return true;
        }

        public override void OnClose()
        {
            base.OnClose();
        }

        void HandleSendAuthSession()
        {
            AuthChallenge challenge = new();
            challenge.Challenge = _serverChallenge;
            challenge.DosChallenge = new byte[32].GenerateRandomKey(32);
            challenge.DosZeroBits = 1;

            SendPacket(challenge);
        }

        void HandleAuthSession(AuthSession authSession)
        {
            HandleAuthSessionCallback(authSession);
        }

        void HandleAuthSessionCallback(AuthSession authSession)
        {
            RealmBuildInfo buildInfo = Global.RealmMgr.GetBuildInfo(BNetServer.Networking.Session.LastSessionData.Build);
            if (buildInfo == null)
            {
                SendAuthResponseError(BattlenetRpcErrorCode.BadVersion);
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthSessionCallback: Missing auth seed for realm build {BNetServer.Networking.Session.LastSessionData.Build} ({GetRemoteIpAddress()}).");
                CloseSocket();
                return;
            }

            AccountInfo account = new();

            // For hook purposes, we get Remoteaddress at this point.
            var address = GetRemoteIpAddress();

            Sha256 digestKeyHash = new();
            digestKeyHash.Process(account.game.SessionKey, account.game.SessionKey.Length);
            if (account.game.OS == "Wn64")
                digestKeyHash.Finish(buildInfo.Win64AuthSeed);
            else if (account.game.OS == "Mc64")
                digestKeyHash.Finish(buildInfo.Mac64AuthSeed);
            else
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthSession: Unknown OS for account: {account.game.Id} ('{authSession.RealmJoinTicket}') address: {address}");
                CloseSocket();
                return;
            }

            HmacSha256 hmac = new(digestKeyHash.Digest);
            hmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
            hmac.Process(_serverChallenge, 16);
            hmac.Finish(AuthCheckSeed, 16);

            // Check that Key and account name are the same on client and server
            if (!hmac.Digest.Compare(authSession.Digest))
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthSession: Authentication failed for account: {account.game.Id} ('{authSession.RealmJoinTicket}') address: {address}");
                CloseSocket();
                return;
            }

            Sha256 keyData = new();
            keyData.Finish(account.game.SessionKey);

            HmacSha256 sessionKeyHmac = new(keyData.Digest);
            sessionKeyHmac.Process(_serverChallenge, 16);
            sessionKeyHmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
            sessionKeyHmac.Finish(SessionKeySeed, 16);

            _sessionKey = new byte[40];
            var sessionKeyGenerator = new SessionKeyGenerator(sessionKeyHmac.Digest, 32);
            sessionKeyGenerator.Generate(_sessionKey, 40);

            HmacSha256 encryptKeyGen = new(_sessionKey);
            encryptKeyGen.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
            encryptKeyGen.Process(_serverChallenge, 16);
            encryptKeyGen.Finish(EncryptionKeySeed, 16);

            // only first 16 bytes of the hmac are used
            Buffer.BlockCopy(encryptKeyGen.Digest, 0, _encryptKey, 0, 16);

            // As we don't know if attempted login process by ip works, we update last_attempt_ip right away
            //PreparedStatement stmt = DB.Login.GetPreparedStatement(LoginStatements.UPD_LAST_ATTEMPT_IP);
            //stmt.AddValue(0, address.Address.ToString());
            //stmt.AddValue(1, authSession.RealmJoinTicket);
            //DB.Login.Execute(stmt);

            // This also allows to check for possible "hack" attempts on account
            //stmt = DB.Login.GetPreparedStatement(LoginStatements.UPD_ACCOUNT_INFO_CONTINUED_SESSION);
            //stmt.AddValue(0, _sessionKey);
            //stmt.AddValue(1, account.game.Id);
            //DB.Login.Execute(stmt);

            //Re-check ip locking (same check as in auth).
            if (account.battleNet.IsLockedToIP) // if ip is locked
            {
                if (account.battleNet.LastIP != address.Address.ToString())
                {
                    SendAuthResponseError(BattlenetRpcErrorCode.RiskAccountLocked);
                    Log.Print(LogType.Error, "HandleAuthSession: Sent Auth Response (Account IP differs).");
                    CloseSocket();
                    return;
                }
            }

            if (account.IsBanned()) // if account banned
            {
                SendAuthResponseError(BattlenetRpcErrorCode.GameAccountBanned);
                Log.Print(LogType.Error, "WorldSocket:HandleAuthSession: Sent Auth Response (Account banned).");
                CloseSocket();
                return;
            }

            Log.Print(LogType.Server, $"WorldSocket:HandleAuthSession: Client '{authSession.RealmJoinTicket}' authenticated successfully from {address}.");

            // Update the last_ip in the database
            //stmt = DB.Login.GetPreparedStatement(LoginStatements.UPD_LAST_IP);
            //stmt.AddValue(0, address.Address.ToString());
            //stmt.AddValue(1, authSession.RealmJoinTicket);
            //DB.Login.Execute(stmt);

            _realmId = new RealmId((byte)authSession.RegionID, (byte)authSession.BattlegroupID, authSession.RealmID);
            if (!HermesProxy.World.Client.WorldClient.ConnectToWorldServer(RealmManager.Instance.GetRealm(_realmId)))
            {
                SendAuthResponseError(BattlenetRpcErrorCode.BadServer);
                Log.Print(LogType.Error, "The WorldClient failed to connect to the selected world server!");
                CloseSocket();
                return;
            }

            SendPacket(new EnterEncryptedMode(_encryptKey, true));
            AsyncRead();
        }

        public struct ConnectToKey
        {
            public ulong Raw
            {
                get { return ((ulong)AccountId | ((ulong)connectionType << 32) | (Key << 33)); }
                set
                {
                    AccountId = (uint)(value & 0xFFFFFFFF);
                    connectionType = (ConnectionType)((value >> 32) & 1);
                    Key = (value >> 33);
                }
            }

            public uint AccountId;
            public ConnectionType connectionType;
            public ulong Key;
        }

        void HandleAuthContinuedSession(AuthContinuedSession authSession)
        {
            ConnectToKey key = new();
            _key = key.Raw = authSession.Key;

            _connectType = key.connectionType;
            if (_connectType != ConnectionType.Instance)
            {
                SendAuthResponseError(BattlenetRpcErrorCode.Denied);
                CloseSocket();
                return;
            }

            HandleAuthContinuedSessionCallback(authSession);
        }

        void HandleAuthContinuedSessionCallback(AuthContinuedSession authSession)
        {
            ConnectToKey key = new();
            _key = key.Raw = authSession.Key;

            uint accountId = key.AccountId;
            string login = BNetServer.Networking.Session.LastSessionData.AccountInfo.Login;
            _sessionKey = BNetServer.Networking.Session.LastSessionData.SessionKey;

            HmacSha256 hmac = new(_sessionKey);
            hmac.Process(BitConverter.GetBytes(authSession.Key), 8);
            hmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Length);
            hmac.Process(_serverChallenge, 16);
            hmac.Finish(ContinuedSessionSeed, 16);

            if (!hmac.Digest.Compare(authSession.Digest))
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthContinuedSession: Authentication failed for account: {accountId} ('{login}') address: {GetRemoteIpAddress()}");
                CloseSocket();
                return;
            }

            HmacSha256 encryptKeyGen = new(_sessionKey);
            encryptKeyGen.Process(authSession.LocalChallenge, authSession.LocalChallenge.Length);
            encryptKeyGen.Process(_serverChallenge, 16);
            encryptKeyGen.Finish(EncryptionKeySeed, 16);

            // only first 16 bytes of the hmac are used
            Buffer.BlockCopy(encryptKeyGen.Digest, 0, _encryptKey, 0, 16);

            SendPacket(new EnterEncryptedMode(_encryptKey, true));
            AsyncRead();
        }

        public void SendConnectToInstance(ConnectToSerial serial)
        {
            var instanceAddress = GetRemoteIpAddress();

            _instanceConnectKey.AccountId = BNetServer.Networking.Session.LastSessionData.AccountInfo.Id;
            _instanceConnectKey.connectionType = ConnectionType.Instance;
            _instanceConnectKey.Key = RandomHelper.URand(0, 0x7FFFFFFF);

            ConnectTo connectTo = new();
            connectTo.Key = _instanceConnectKey.Raw;
            connectTo.Serial = serial;
            connectTo.Payload.Port = (ushort)8086;
            connectTo.Con = (byte)ConnectionType.Instance;

            if (instanceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                connectTo.Payload.Where.IPv4 = instanceAddress.Address.GetAddressBytes();
                connectTo.Payload.Where.Type = ConnectTo.AddressType.IPv4;
            }
            else
            {
                connectTo.Payload.Where.IPv6 = instanceAddress.Address.GetAddressBytes();
                connectTo.Payload.Where.Type = ConnectTo.AddressType.IPv6;
            }

            SendPacket(connectTo);
        }
        public class CharacterLoginFailed : ServerPacket
        {
            public CharacterLoginFailed(LoginFailureReason code) : base((uint)Opcode.SMSG_CHARACTER_LOGIN_FAILED)
            {
                Code = code;
            }

            public override void Write()
            {
                _worldPacket.WriteUInt8((byte)Code);
            }

            LoginFailureReason Code;
        }
        public void AbortLogin(LoginFailureReason reason)
        {
            SendPacket(new CharacterLoginFailed(reason));

        }
        void HandleConnectToFailed(ConnectToFailed connectToFailed)
        {
            switch (connectToFailed.Serial)
            {
                case ConnectToSerial.WorldAttempt1:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt2);
                    break;
                case ConnectToSerial.WorldAttempt2:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt3);
                    break;
                case ConnectToSerial.WorldAttempt3:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt4);
                    break;
                case ConnectToSerial.WorldAttempt4:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt5);
                    break;
                case ConnectToSerial.WorldAttempt5:
                    {
                        Log.Print(LogType.Error, "Failed to connect 5 times to world socket, aborting login");
                        AbortLogin(LoginFailureReason.NoWorld);
                        break;
                    }
                default:
                    return;
            }

        }

        void HandleEnterEncryptedModeAck()
        {
            _worldCrypt.Initialize(_encryptKey);
            if (_connectType == ConnectionType.Realm)
            {
                SendAuthResponse(BattlenetRpcErrorCode.Ok, false);
                SendSetTimeZoneInformation();
                SendFeatureSystemStatusGlueScreen();
                SendClientCacheVersion(0);
                SendBnetConnectionState(1);
            }
            //else
            //    Global.WorldMgr.AddInstanceSocket(this, _key);
        }

        public void SendAuthResponseError(BattlenetRpcErrorCode code)
        {
            AuthResponse response = new();
            response.SuccessInfo.HasValue = false;
            response.WaitInfo.HasValue = false;
            response.Result = code;
            SendPacket(response);
        }

        public void SendAuthResponse(BattlenetRpcErrorCode code, bool queued, uint queuePos = 0)
        {
            AuthResponse response = new();
            response.Result = code;

            if (code == BattlenetRpcErrorCode.Ok)
            {
                response.SuccessInfo.HasValue = true;

                response.SuccessInfo.Value = new AuthResponse.AuthSuccessInfo();
                response.SuccessInfo.Value.ActiveExpansionLevel = (byte)0;
                response.SuccessInfo.Value.AccountExpansionLevel = (byte)0;
                response.SuccessInfo.Value.VirtualRealmAddress = _realmId.GetAddress();
                response.SuccessInfo.Value.Time = (uint)Time.UnixTime;

                var realm = RealmManager.Instance.GetRealm(_realmId);

                // Send current home realm. Also there is no need to send it later in realm queries.
                response.SuccessInfo.Value.VirtualRealms.Add(new VirtualRealmInfo(realm.Id.GetAddress(), true, false, realm.Name, realm.NormalizedName));

                List<RaceClassAvailability> availableRaces = new List<RaceClassAvailability>();
                RaceClassAvailability race = new RaceClassAvailability();

                race.RaceID = 1;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(2, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 2;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 3;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(2, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 4;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(11, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 5;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 6;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(11, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 7;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 8;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                availableRaces.Add(race);

                response.SuccessInfo.Value.AvailableClasses = availableRaces;
            }

            if (queued)
            {
                response.WaitInfo.HasValue = true;
                response.WaitInfo.Value.WaitCount = queuePos;
            }

            SendPacket(response);
        }

        public void SendAuthWaitQue(uint position)
        {
            if (position != 0)
            {
                WaitQueueUpdate waitQueueUpdate = new();
                waitQueueUpdate.WaitInfo.WaitCount = position;
                waitQueueUpdate.WaitInfo.WaitTime = 0;
                waitQueueUpdate.WaitInfo.HasFCM = false;
                SendPacket(waitQueueUpdate);
            }
            else
                SendPacket(new WaitQueueFinish());
        }

        public void SendSetTimeZoneInformation()
        {
            // @todo: replace dummy values
            SetTimeZoneInformation packet = new();
            packet.ServerTimeTZ = "Europe/Paris";
            packet.GameTimeTZ = "Europe/Paris";

            SendPacket(packet);//enabled it
        }

        public void SendFeatureSystemStatusGlueScreen()
        {
            FeatureSystemStatusGlueScreen features = new();
            features.BpayStoreAvailable = false;
            features.BpayStoreDisabledByParentalControls = false;
            features.CharUndeleteEnabled = true;
            features.BpayStoreEnabled = false;
            features.MaxCharactersPerRealm = 10;
            features.MinimumExpansionLevel = 5;
            features.MaximumExpansionLevel = 8;

            var europaTicketConfig = new EuropaTicketConfig();
            europaTicketConfig.ThrottleState.MaxTries = 10;
            europaTicketConfig.ThrottleState.PerMilliseconds = 60000;
            europaTicketConfig.ThrottleState.TryCount = 1;
            europaTicketConfig.ThrottleState.LastResetTimeBeforeNow = 111111;
            europaTicketConfig.TicketsEnabled = true;
            europaTicketConfig.BugsEnabled = true;
            europaTicketConfig.ComplaintsEnabled = true;
            europaTicketConfig.SuggestionsEnabled = true;

            features.EuropaTicketSystemStatus.Set(europaTicketConfig);

            SendPacket(features);
        }

        public void SendClientCacheVersion(uint version)
        {
            ClientCacheVersion cache = new();
            cache.CacheVersion = version;
            SendPacket(cache);//enabled it
        }

        public void SendBnetConnectionState(byte state)
        {
            ConnectionStatus bnetConnected = new();
            bnetConnected.State = state;
            SendPacket(bnetConnected);
        }

        public void SendCharEnum()
        {
            EnumCharactersResult charEnum = new();
            charEnum.Success = true;
            charEnum.IsDeletedCharacters = false;
            charEnum.IsNewPlayerRestrictionSkipped = false;
            charEnum.IsNewPlayerRestricted = false;
            charEnum.IsNewPlayer = true;
            charEnum.IsAlliedRacesCreationAllowed = false;

            EnumCharactersResult.CharacterInfo char1 = new EnumCharactersResult.CharacterInfo();
            char1.Guid = new HermesProxy.WowGuid128(Framework.Constants.World.HighGuidType703.Player, 1);
            Console.WriteLine("Guid is " + char1.Guid.ToString());
            char1.RaceId = 7;
            char1.ClassId = 1;
            char1.SexId = 0;
            char1.ExperienceLevel = 255;
            char1.ZoneId = 148;
            char1.MapId = 1;
            char1.PreloadPos = new Framework.GameMath.Vector3(6419.912f, 513.763f, 8.653581f);
            char1.GuildGuid = new HermesProxy.WowGuid128();
            char1.Flags = CharacterFlags.Unk2 | CharacterFlags.HideHelm | CharacterFlags.HideCloak | CharacterFlags.Unk29;
            char1.Flags2 = 402685956;
            char1.Flags3 = 855688192;
            char1.PetCreatureDisplayId = 0;
            char1.PetExperienceLevel = 0;
            char1.PetCreatureFamilyId = 0;
            char1.ProfessionIds[0] = 186; // mining
            char1.ProfessionIds[1] = 333; // enchanting

            char1.VisualItems[EquipmentSlot.Head].DisplayId = 34216;
            char1.VisualItems[EquipmentSlot.Head].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Head].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Head].InvType = 1;
            char1.VisualItems[EquipmentSlot.Head].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Neck].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Neck].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Neck].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Neck].InvType = 0;
            char1.VisualItems[EquipmentSlot.Neck].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Shoulders].DisplayId = 34258;
            char1.VisualItems[EquipmentSlot.Shoulders].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Shoulders].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Shoulders].InvType = 3;
            char1.VisualItems[EquipmentSlot.Shoulders].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Shirt].DisplayId = 9880;
            char1.VisualItems[EquipmentSlot.Shirt].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Shirt].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Shirt].InvType = 4;
            char1.VisualItems[EquipmentSlot.Shirt].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Chest].DisplayId = 33635;
            char1.VisualItems[EquipmentSlot.Chest].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Chest].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Chest].InvType = 5;
            char1.VisualItems[EquipmentSlot.Chest].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Waist].DisplayId = 33633;
            char1.VisualItems[EquipmentSlot.Waist].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Waist].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Waist].InvType = 6;
            char1.VisualItems[EquipmentSlot.Waist].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Legs].DisplayId = 33637;
            char1.VisualItems[EquipmentSlot.Legs].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Legs].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Legs].InvType = 7;
            char1.VisualItems[EquipmentSlot.Legs].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Feet].DisplayId = 33639;
            char1.VisualItems[EquipmentSlot.Feet].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Feet].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Feet].InvType = 8;
            char1.VisualItems[EquipmentSlot.Feet].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Wrist].DisplayId = 33634;
            char1.VisualItems[EquipmentSlot.Wrist].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Wrist].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Wrist].InvType = 9;
            char1.VisualItems[EquipmentSlot.Wrist].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Hands].DisplayId = 33636;
            char1.VisualItems[EquipmentSlot.Hands].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Hands].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Hands].InvType = 10;
            char1.VisualItems[EquipmentSlot.Hands].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Finger1].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Finger1].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Finger1].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Finger1].InvType = 0;
            char1.VisualItems[EquipmentSlot.Finger1].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Finger2].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Finger2].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Finger2].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Finger2].InvType = 0;
            char1.VisualItems[EquipmentSlot.Finger2].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Trinket1].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Trinket1].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Trinket1].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Trinket1].InvType = 0;
            char1.VisualItems[EquipmentSlot.Trinket1].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Trinket2].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Trinket2].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Trinket2].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Trinket2].InvType = 0;
            char1.VisualItems[EquipmentSlot.Trinket2].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Cloak].DisplayId = 23115;
            char1.VisualItems[EquipmentSlot.Cloak].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Cloak].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Cloak].InvType = 16;
            char1.VisualItems[EquipmentSlot.Cloak].Subclass = 0;

            char1.VisualItems[EquipmentSlot.MainHand].DisplayId = 23875;
            char1.VisualItems[EquipmentSlot.MainHand].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.MainHand].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.MainHand].InvType = 17;
            char1.VisualItems[EquipmentSlot.MainHand].Subclass = 0;

            char1.VisualItems[EquipmentSlot.OffHand].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.OffHand].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.OffHand].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.OffHand].InvType = 0;
            char1.VisualItems[EquipmentSlot.OffHand].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Ranged].DisplayId = 1137;
            char1.VisualItems[EquipmentSlot.Ranged].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Ranged].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Ranged].InvType = 26;
            char1.VisualItems[EquipmentSlot.Ranged].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Tabard].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Tabard].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Tabard].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Tabard].InvType = 0;
            char1.VisualItems[EquipmentSlot.Tabard].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Bag1].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Bag1].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Bag1].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Bag1].InvType = 18;
            char1.VisualItems[EquipmentSlot.Bag1].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Bag2].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Bag2].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Bag2].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Bag2].InvType = 18;
            char1.VisualItems[EquipmentSlot.Bag2].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Bag3].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Bag3].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Bag3].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Bag3].InvType = 18;
            char1.VisualItems[EquipmentSlot.Bag3].Subclass = 0;

            char1.VisualItems[EquipmentSlot.Bag4].DisplayId = 0;
            char1.VisualItems[EquipmentSlot.Bag4].DisplayEnchantId = 0;
            char1.VisualItems[EquipmentSlot.Bag4].SecondaryItemModifiedAppearanceID = 0;
            char1.VisualItems[EquipmentSlot.Bag4].InvType = 18;
            char1.VisualItems[EquipmentSlot.Bag4].Subclass = 0;

            char1.LastPlayedTime = Time.UnixTime;
            char1.SpecID = 0;
            char1.Unknown703 = 55;
            char1.LastLoginVersion = 11400;
            char1.Flags4 = 0;
            char1.OverrideSelectScreenFileDataID = 0;

            char1.Customizations = new Array<ChrCustomizationChoice>(5);
            char1.Customizations[0] = new ChrCustomizationChoice(80, 17736);
            char1.Customizations[1] = new ChrCustomizationChoice(81, 17741);
            char1.Customizations[2] = new ChrCustomizationChoice(82, 17751);
            char1.Customizations[3] = new ChrCustomizationChoice(83, 17758);
            char1.Customizations[4] = new ChrCustomizationChoice(84, 17764);

            char1.FirstLogin = false;
            char1.BoostInProgress = false;
            char1.unkWod61x = 0;
            char1.Name = "Oroxzy";
            charEnum.Characters.Add(char1);

            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(1, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(2, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(3, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(4, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(5, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(6, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(7, true, false, true));
            charEnum.RaceUnlockData.Add(new EnumCharactersResult.RaceUnlock(8, true, false, true));


            SendPacket(charEnum);
        }

        bool HandlePing(Ping ping)
        {
            if (_LastPingTime == 0)
                _LastPingTime = Time.UnixTime; // for 1st ping
            else
            {
                long now = Time.UnixTime;
                long diff = now - _LastPingTime;
                _LastPingTime = now;
            }

            SendPacket(new Pong(ping.Serial));
            return true;
        }
    }

    class AccountInfo
    {
        public AccountInfo()
        {
            game.Id = BNetServer.Networking.Session.LastSessionData.GameAccountInfo.Id;
            game.SessionKey = BNetServer.Networking.Session.LastSessionData.SessionKey;
            battleNet.LastIP = BNetServer.Networking.Session.LastSessionData.AccountInfo.LastIP;
            battleNet.IsLockedToIP = BNetServer.Networking.Session.LastSessionData.AccountInfo.IsLockedToIP;
            battleNet.LockCountry = BNetServer.Networking.Session.LastSessionData.AccountInfo.LockCountry;
            game.Expansion = 9;
            game.MuteTime = 0;
            battleNet.Locale = BNetServer.Networking.Session.LastSessionData.Locale.ToEnum<Locale>();
            game.Recruiter = 0;
            game.OS = BNetServer.Networking.Session.LastSessionData.OS;
            battleNet.Id = BNetServer.Networking.Session.LastSessionData.AccountInfo.Id;
            battleNet.IsBanned = BNetServer.Networking.Session.LastSessionData.AccountInfo.IsBanned;
            game.IsBanned = BNetServer.Networking.Session.LastSessionData.GameAccountInfo.IsBanned;
            game.IsRectuiter = false;

            if (battleNet.Locale >= Locale.Total)
                battleNet.Locale = Locale.enUS;
        }

        public bool IsBanned() { return battleNet.IsBanned || game.IsBanned; }

        public BattleNet battleNet;
        public Game game;

        public struct BattleNet
        {
            public uint Id;
            public bool IsLockedToIP;
            public string LastIP;
            public string LockCountry;
            public Locale Locale;
            public bool IsBanned;
        }

        public struct Game
        {
            public uint Id;
            public byte[] SessionKey;
            public byte Expansion;
            public long MuteTime;
            public string OS;
            public uint Recruiter;
            public bool IsRectuiter;
            public bool IsBanned;
        }
    }

    enum ReadDataHandlerResult
    {
        Ok = 0,
        Error = 1,
        WaitingForQuery = 2
    }
}
