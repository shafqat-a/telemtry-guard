namespace TelemetryGuard.RiskEngine.Contracts;

/// <summary>Coarse classification of the autonomous system an IP belongs to.
/// Drives carrier-grade-NAT normalization of velocity features and the
/// ip_datacenter_asn T1 rule. One-hot encoded when fed to a model.</summary>
public enum AsnType
{
    Unknown = 0,     // enrichment DB missing or ASN not classified
    Residential = 1, // consumer ISP (IP2Proxy usage_type ISP)
    Mobile = 2,      // mobile carrier (usage_type MOB) — carrier-grade NAT expected
    Business = 3,    // corporate egress (usage_type COM/ORG)
    Datacenter = 4,  // hosting/cloud (usage_type DCH or seed ASN list)
    Education = 5,   // universities/libraries (usage_type EDU/LIB)
    Government = 6,  // usage_type GOV/MIL
    Cdn = 7          // usage_type CDN
}
