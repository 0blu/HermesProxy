﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HermesProxy.World
{
    public class SniffFile
    {
        public SniffFile(string fileName, ushort build)
        {
            _fileWriter = new System.IO.BinaryWriter(File.Open(fileName + "_" + build + "_" + Time.UnixTime + ".pkt", FileMode.Create));
            _gameVersion = build;
        }
        BinaryWriter _fileWriter;
        ushort _gameVersion;
        private System.Threading.Mutex mut = new System.Threading.Mutex();

        public void WriteHeader()
        {
            _fileWriter.Write('P');
            _fileWriter.Write('K');
            _fileWriter.Write('T');
            UInt16 sniffVersion = 0x201;
            _fileWriter.Write(sniffVersion);
            _fileWriter.Write(_gameVersion);

            for (int i = 0; i < 40; i++)
            {
                byte zero = 0;
                _fileWriter.Write(zero);
            }
        }

        public void WritePacket(uint opcode, bool isFromClient, byte[] data)
        {
            mut.WaitOne();

            byte direction = !isFromClient ? (byte)0xff : (byte)0x0;
            _fileWriter.Write(direction);

            uint unixtime = (uint)Time.UnixTime;
            _fileWriter.Write(unixtime);
            _fileWriter.Write(unixtime); // tick count

            if (isFromClient)
            {
                uint packetSize = (uint)data.Length + sizeof(uint);
                _fileWriter.Write(packetSize);
                _fileWriter.Write(opcode);

                Console.WriteLine("Write Client " + opcode + " Size " + packetSize);
            }
            else
            {
                uint packetSize = (uint)data.Length + sizeof(ushort);
                _fileWriter.Write(packetSize);
                ushort opcode2 = (ushort)opcode;
                _fileWriter.Write(opcode2);

                Console.WriteLine("Write Server " + opcode + " Size " + packetSize);
            }
            _fileWriter.Write(data);
            mut.ReleaseMutex();
        }

        public void CloseFile()
        {
            _fileWriter.Close();
        }
    }
}
