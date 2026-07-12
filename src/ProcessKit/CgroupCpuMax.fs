namespace ProcessKit

open System

module internal CgroupCpuMax =

    [<Literal>]
    let internal PeriodMicroseconds = 100_000

    let calculateQuota (cores: float) : float =
        max 1.0 (Math.Round(cores * float PeriodMicroseconds))

    let isQuotaOverflow (quotaMicroseconds: float) : bool =
        Double.IsNaN quotaMicroseconds
        || Double.IsInfinity quotaMicroseconds
        || quotaMicroseconds >= float Int64.MaxValue

    let formatCpuMax (quotaMicroseconds: float) : string =
        $"{int64 quotaMicroseconds} {PeriodMicroseconds}"
