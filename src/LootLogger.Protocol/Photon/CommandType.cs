// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Photon;

internal enum CommandType
{
    Disconnect = 4,
    SendReliable = 6,
    SendUnreliable = 7,
    SendFragment = 8
}