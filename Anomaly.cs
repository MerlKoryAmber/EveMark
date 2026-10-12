namespace JitaWatch;

public static class Anomaly
{
    public static bool IsSuspicious(BoardRow row, IReadOnlyList<(string Day, long Vol, decimal Avg)>? hist30)
    {
        if (hist30 is { Count: >= 5 })
        {
            var avgs = hist30.Select(h => h.Avg).ToList();
            var mean = avgs.Average();
            var variance = avgs.Average(a => (double)((a - mean) * (a - mean)));
            var sigma = (decimal)Math.Sqrt(variance);
            if (sigma > 0)
            {
                var mid = (row.BestBuy + row.BestSell) / 2m;
                if (Math.Abs(mid - mean) > 3m * sigma)
                    return true;
            }

            var vols = hist30.Select(h => (double)h.Vol).ToList();
            var vMean = vols.Average();
            if (vMean > 0 && row.DailyVolume is long dv && dv > vMean * 4)
                return true;
        }

        // стена: одна сторона крошечная относительно другой при огромной марже
        if (row.MarginFrac > 0.5m && row.BuyTouch is long bt && row.SellVolume > 0 && bt * 20 < row.SellVolume)
            return true;
        if (row.MarginFrac > 0.5m && row.SellTouch is long st && row.BuyVolume > 0 && st * 20 < row.BuyVolume)
            return true;

        return false;
    }
}
