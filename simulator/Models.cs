namespace F1.Simulator;

public sealed class Driver
{
    public Driver(string code, string team, string color, double skill, double burn, double battery)
    {
        Code = code; Team = team; Color = color; Skill = skill; Burn = burn; Battery = battery;
    }

    public string Code { get; }
    public string Team { get; }
    public string Color { get; }
    public double Skill { get; }
    public double Burn { get; }

    public double Speed { get; set; } = 300;
    public double Progress { get; set; }
    public double LapElapsed { get; set; }
    public double Deg { get; set; }
    public double Fuel { get; set; } = 100;
    public double Temp { get; set; } = 95;
    public double Battery { get; set; }
    public int Laps { get; set; }
    public int Nitro { get; set; }
    public int PitTicks { get; set; }
    public int EngineTicks { get; set; }
    public int PitStops { get; set; }
    public bool Retired { get; set; }
    public HashSet<string> Flags { get; } = new();
}

public sealed record LogEntry(string Time, string Level, string Message);

public sealed record DriverDto(
    string Code, string Team, string Color, double Speed, double Battery, double Deg,
    double Fuel, double Temp, int Nitro, int Laps, int PitStops, bool Retired, bool InPit, bool EngineDown);

public sealed class ProcessorStatus
{
    public bool Reachable { get; set; }
    public bool Frozen { get; set; }
    public int SlowTicks { get; set; }
    public bool Leak { get; set; }
    public long Backlog { get; set; }
    public double MemoryMiB { get; set; }
    public double UptimeSeconds { get; set; }
}

public sealed record StatusDto(
    string Clock, bool Rain, bool SafetyCar, bool Burst, DriverDto[] Drivers, ProcessorStatus Processor);
