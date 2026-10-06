// Adapted from AlbionOnline-StatisticsAnalysis by Triky313 (GPL-3.0)
// https://github.com/Triky313/AlbionOnline-StatisticsAnalysis
namespace LootLogger.Protocol.Photon;

internal readonly record struct PhotonFragmentKey(short PeerId, int Challenge, byte ChannelId, int StartSequenceNumber);