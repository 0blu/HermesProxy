using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_GM_TICKET_CREATE)]
        void HandleGmTicketCreate(WorldPacket packet)
        {
            var response = (LegacyGmTicketResponse) packet.ReadUInt32();
            bool isError = !(response is LegacyGmTicketResponse.CreateSuccess or LegacyGmTicketResponse.UpdateSuccess);
            Session.SendHermesTextMessage($"GM Ticket Status: {response}", isError);

            if (response == LegacyGmTicketResponse.CreateSuccess)
            {
                WorldPacket reply = new WorldPacket(Opcode.CMSG_GM_TICKET_GET_TICKET);
                SendPacketToServer(reply);
            }
        }

        [PacketHandler(Opcode.SMSG_GM_TICKET_SYSTEM_STATUS)]
        void HandleGmTicketSystemStatus(WorldPacket packet)
        {
            GMTicketSystemStatusPkt status = new GMTicketSystemStatusPkt();
            status.Status = packet.ReadInt32();
            SendPacketToClient(status);
        }

        [PacketHandler(Opcode.SMSG_GM_TICKET_DELETE_TICKET)]
        void HandleGmTicketDeleteTicket(WorldPacket packet)
        {
            GMTicketCaseStatus tickets = new GMTicketCaseStatus();
            packet.ReadUInt32(); // response
            SendPacketToClient(tickets);
        }

    }
}
