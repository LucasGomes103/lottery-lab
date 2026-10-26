namespace LotteryLab.Api.Services;

// Fechamento (desdobramento) da Mega-Sena: dado um conjunto de N dezenas escolhidas,
// monta jogos de 6 dezenas que garantem um prêmio mínimo SE uma certa quantidade das
// dezenas escolhidas for sorteada. Ex.: "quadra se sair 4" = se 4 das N dezenas saírem,
// pelo menos um jogo tem as 4. É um problema de covering design C(v,6,t,m); usamos
// greedy com desempate aleatório + várias tentativas + remoção de jogos redundantes.
// Não aumenta o retorno esperado: só troca chance de prêmio grande por certeza de prêmio menor.
public sealed record WheelGuarantee(string Code, string Label, int Prize, int IfDrawn);

public sealed record WheelResult(
    IReadOnlyList<int> Numbers,
    WheelGuarantee Guarantee,
    IReadOnlyList<int[]> Games,
    long FullCombinations);

public sealed class MegaSenaWheelService
{
    public const int GameSize = 6;
    public const int MinNumbers = 7;
    public const int MaxNumbers = 20;
    public const int MaxGames = 500;

    public static readonly IReadOnlyList<WheelGuarantee> Guarantees = new[]
    {
        new WheelGuarantee("4se4", "Quadra se sair 4", 4, 4),
        new WheelGuarantee("4se5", "Quadra se sair 5", 4, 5),
        new WheelGuarantee("4se6", "Quadra se sair 6", 4, 6),
        new WheelGuarantee("5se5", "Quina se sair 5", 5, 5),
        new WheelGuarantee("5se6", "Quina se sair 6", 5, 6),
    };

    public static WheelGuarantee? FindGuarantee(string code) =>
        Guarantees.FirstOrDefault(g => string.Equals(g.Code, code, StringComparison.OrdinalIgnoreCase));

    public WheelResult Build(IEnumerable<int> numbers, string guaranteeCode, int seed = 0, int attempts = 6)
    {
        var nums = numbers.Distinct().OrderBy(n => n).ToArray();
        if (nums.Any(n => n < 1 || n > 60))
            throw new ArgumentException("As dezenas devem estar entre 1 e 60.");
        if (nums.Length < MinNumbers || nums.Length > MaxNumbers)
            throw new ArgumentException($"Escolha entre {MinNumbers} e {MaxNumbers} dezenas diferentes.");
        var guarantee = FindGuarantee(guaranteeCode)
            ?? throw new ArgumentException("Garantia inválida.");

        var v = nums.Length;
        var t = guarantee.Prize;
        var m = guarantee.IfDrawn;

        // Índices 0..v-1 como bitmask; blocos = jogos de 6, alvos = conjuntos de m dezenas sorteadas.
        var blocks = Combinations(v, GameSize).ToArray();
        var targets = Combinations(v, m).ToArray();
        var targetIndex = new Dictionary<int, int>(targets.Length);
        for (var i = 0; i < targets.Length; i++) targetIndex[targets[i]] = i;

        // Alvos cobertos por cada bloco: |bloco ∩ alvo| >= t.
        var full = (1 << v) - 1;
        var covers = new int[blocks.Length][];
        var coveredBy = new List<int>[targets.Length];
        for (var i = 0; i < targets.Length; i++) coveredBy[i] = new List<int>();
        var buffer = new List<int>();
        for (var b = 0; b < blocks.Length; b++)
        {
            buffer.Clear();
            var inside = blocks[b];
            var outside = full & ~inside;
            for (var k = t; k <= Math.Min(m, GameSize); k++)
            {
                if (m - k > v - GameSize) continue;
                foreach (var a in SubsetsOf(inside, k))
                foreach (var o in SubsetsOf(outside, m - k))
                    buffer.Add(targetIndex[a | o]);
            }
            covers[b] = buffer.ToArray();
            foreach (var target in covers[b]) coveredBy[target].Add(b);
        }

        int[]? best = null;
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            var chosen = Greedy(blocks.Length, targets.Length, covers, coveredBy, new Random(seed + attempt * 7919));
            RemoveRedundant(chosen, targets.Length, covers);
            if (best is null || chosen.Count < best.Length) best = chosen.ToArray();
        }

        if (best!.Length > MaxGames)
            throw new ArgumentException($"Esse fechamento precisa de {best.Length} jogos (máximo {MaxGames}). Escolha menos dezenas ou uma garantia menor.");

        var games = best
            .Select(b => Enumerable.Range(0, v).Where(i => (blocks[b] & (1 << i)) != 0).Select(i => nums[i]).ToArray())
            .OrderBy(g => string.Join(",", g.Select(n => n.ToString("D2"))))
            .ToList();

        return new WheelResult(nums, guarantee, games, Binomial(v, GameSize));
    }

    // Confere a garantia por força bruta (usado em testes e como checagem de sanidade).
    public static bool VerifyGuarantee(IReadOnlyList<int> numbers, IReadOnlyList<int[]> games, WheelGuarantee guarantee)
    {
        var v = numbers.Count;
        var masks = games.Select(g => g.Aggregate(0, (acc, n) => acc | (1 << IndexOf(numbers, n)))).ToArray();
        foreach (var drawn in Combinations(v, guarantee.IfDrawn))
            if (!masks.Any(g => PopCount(g & drawn) >= guarantee.Prize)) return false;
        return true;
    }

    private static List<int> Greedy(int blockCount, int targetCount, int[][] covers, List<int>[] coveredBy, Random rnd)
    {
        var gain = new int[blockCount];
        for (var b = 0; b < blockCount; b++) gain[b] = covers[b].Length;
        var covered = new bool[targetCount];
        var left = targetCount;
        var chosen = new List<int>();
        var ties = new List<int>();
        while (left > 0)
        {
            var bestGain = 0;
            ties.Clear();
            for (var b = 0; b < blockCount; b++)
            {
                if (gain[b] > bestGain) { bestGain = gain[b]; ties.Clear(); ties.Add(b); }
                else if (gain[b] == bestGain && bestGain > 0) ties.Add(b);
            }
            var pick = ties[rnd.Next(ties.Count)];
            chosen.Add(pick);
            foreach (var target in covers[pick])
            {
                if (covered[target]) continue;
                covered[target] = true;
                left--;
                foreach (var other in coveredBy[target]) gain[other]--;
            }
        }
        return chosen;
    }

    private static void RemoveRedundant(List<int> chosen, int targetCount, int[][] covers)
    {
        var count = new int[targetCount];
        foreach (var b in chosen) foreach (var target in covers[b]) count[target]++;
        for (var i = chosen.Count - 1; i >= 0; i--)
        {
            var b = chosen[i];
            if (covers[b].All(target => count[target] > 1))
            {
                foreach (var target in covers[b]) count[target]--;
                chosen.RemoveAt(i);
            }
        }
    }

    private static IEnumerable<int> Combinations(int n, int k)
    {
        if (k < 0 || k > n) yield break;
        if (k == 0) { yield return 0; yield break; }
        var idx = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            var mask = 0;
            foreach (var i in idx) mask |= 1 << i;
            yield return mask;
            var p = k - 1;
            while (p >= 0 && idx[p] == n - k + p) p--;
            if (p < 0) yield break;
            idx[p]++;
            for (var j = p + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
        }
    }

    private static IEnumerable<int> SubsetsOf(int mask, int k)
    {
        var bits = Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0).ToArray();
        foreach (var c in Combinations(bits.Length, k))
        {
            var sub = 0;
            for (var i = 0; i < bits.Length; i++)
                if ((c & (1 << i)) != 0) sub |= 1 << bits[i];
            yield return sub;
        }
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        throw new ArgumentException($"Dezena {value} não faz parte do fechamento.");
    }

    private static int PopCount(int x) => System.Numerics.BitOperations.PopCount((uint)x);

    private static long Binomial(int n, int k)
    {
        long r = 1;
        for (var i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }
}
