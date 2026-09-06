using System.Collections.Generic;

// Owns disconnect exclusions and score-based winner selection independently of Netcode state.
internal sealed class MatchOutcomeTracker
{
    private readonly Dictionary<ulong, bool> defeatedByDisconnect = new();

    internal void MarkDefeated(ulong clientId)
    {
        // Record one previously validated match departure using the existing exclusion ledger.
        defeatedByDisconnect[clientId] = true;
    }

    internal void Reset()
    {
        // Clear the exclusions at the same new-match and room-idle boundaries as before.
        defeatedByDisconnect.Clear();
    }

    internal List<ulong> ResolveWinners(IReadOnlyDictionary<ulong, int> scores)
    {
        // 동점 허용 정책: 최고 점수자 전원을 우승자로 반환.
        List<ulong> winners = new();

        int topScore = int.MinValue;
        foreach (KeyValuePair<ulong, int> pair in scores)
        {
            if (defeatedByDisconnect.ContainsKey(pair.Key))
            {
                continue;
            }

            if (pair.Value > topScore)
            {
                topScore = pair.Value;
            }
        }

        foreach (KeyValuePair<ulong, int> pair in scores)
        {
            if (defeatedByDisconnect.ContainsKey(pair.Key))
            {
                continue;
            }

            if (pair.Value == topScore)
            {
                winners.Add(pair.Key);
            }
        }

        return winners;
    }
}
