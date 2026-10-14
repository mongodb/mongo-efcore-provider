using MongoDB.Bson;

namespace MongoDB.EntityFrameworkCore.Benchmarks;

// Generated: 40 scalar properties cycling int/long/double/bool/string (a realistic entity width; WideEntity has 400). Do not edit by hand.
public class MediumEntity
{
    public ObjectId Id { get; set; }
    public int Prop001 { get; set; }
    public long Prop002 { get; set; }
    public double Prop003 { get; set; }
    public bool Prop004 { get; set; }
    public string Prop005 { get; set; } = "";
    public int Prop006 { get; set; }
    public long Prop007 { get; set; }
    public double Prop008 { get; set; }
    public bool Prop009 { get; set; }
    public string Prop010 { get; set; } = "";
    public int Prop011 { get; set; }
    public long Prop012 { get; set; }
    public double Prop013 { get; set; }
    public bool Prop014 { get; set; }
    public string Prop015 { get; set; } = "";
    public int Prop016 { get; set; }
    public long Prop017 { get; set; }
    public double Prop018 { get; set; }
    public bool Prop019 { get; set; }
    public string Prop020 { get; set; } = "";
    public int Prop021 { get; set; }
    public long Prop022 { get; set; }
    public double Prop023 { get; set; }
    public bool Prop024 { get; set; }
    public string Prop025 { get; set; } = "";
    public int Prop026 { get; set; }
    public long Prop027 { get; set; }
    public double Prop028 { get; set; }
    public bool Prop029 { get; set; }
    public string Prop030 { get; set; } = "";
    public int Prop031 { get; set; }
    public long Prop032 { get; set; }
    public double Prop033 { get; set; }
    public bool Prop034 { get; set; }
    public string Prop035 { get; set; } = "";
    public int Prop036 { get; set; }
    public long Prop037 { get; set; }
    public double Prop038 { get; set; }
    public bool Prop039 { get; set; }
    public string Prop040 { get; set; } = "";

    public MediumEntity Fill(int i)
    {
        Prop001 = i + 1;
        Prop002 = 1_000_000_000L + i + 2;
        Prop003 = (i + 3) * 0.5;
        Prop004 = ((i + 4) % 2) == 0;
        Prop005 = "p005-" + i;
        Prop006 = i + 6;
        Prop007 = 1_000_000_000L + i + 7;
        Prop008 = (i + 8) * 0.5;
        Prop009 = ((i + 9) % 2) == 0;
        Prop010 = "p010-" + i;
        Prop011 = i + 11;
        Prop012 = 1_000_000_000L + i + 12;
        Prop013 = (i + 13) * 0.5;
        Prop014 = ((i + 14) % 2) == 0;
        Prop015 = "p015-" + i;
        Prop016 = i + 16;
        Prop017 = 1_000_000_000L + i + 17;
        Prop018 = (i + 18) * 0.5;
        Prop019 = ((i + 19) % 2) == 0;
        Prop020 = "p020-" + i;
        Prop021 = i + 21;
        Prop022 = 1_000_000_000L + i + 22;
        Prop023 = (i + 23) * 0.5;
        Prop024 = ((i + 24) % 2) == 0;
        Prop025 = "p025-" + i;
        Prop026 = i + 26;
        Prop027 = 1_000_000_000L + i + 27;
        Prop028 = (i + 28) * 0.5;
        Prop029 = ((i + 29) % 2) == 0;
        Prop030 = "p030-" + i;
        Prop031 = i + 31;
        Prop032 = 1_000_000_000L + i + 32;
        Prop033 = (i + 33) * 0.5;
        Prop034 = ((i + 34) % 2) == 0;
        Prop035 = "p035-" + i;
        Prop036 = i + 36;
        Prop037 = 1_000_000_000L + i + 37;
        Prop038 = (i + 38) * 0.5;
        Prop039 = ((i + 39) % 2) == 0;
        Prop040 = "p040-" + i;
        return this;
    }
}
