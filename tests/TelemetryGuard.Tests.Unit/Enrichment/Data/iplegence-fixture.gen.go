// Generates a tiny Superior-IP-format MMDB fixture for TelemetryGuard's iplegence
// provider unit tests. The record layout mirrors iplegence internal/schema/record.go
// (ToMMDBType): nested country / continent / city / location / subdivisions / postal /
// asn / traits, English names only, false traits and empty fields omitted. traits also
// carries the inferred usage_type / usage_type_source pair.
package main

import (
	"fmt"
	"net"
	"os"

	"github.com/maxmind/mmdbwriter"
	"github.com/maxmind/mmdbwriter/mmdbtype"
)

func names(en string) mmdbtype.Map { return mmdbtype.Map{"en": mmdbtype.String(en)} }

type rec struct {
	prefix string
	m      mmdbtype.Map
}

func main() {
	dest := "iplegence-fixture.mmdb"
	if len(os.Args) > 1 {
		dest = os.Args[1]
	}

	rows := []rec{
		// Full geo + ASN, no traits. ASN 15169 IS in TelemetryGuard's datacenter seed.
		{"8.8.8.0/24", mmdbtype.Map{
			"country":   mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"continent": mmdbtype.Map{"code": mmdbtype.String("NA"), "names": names("North America")},
			"city":      mmdbtype.Map{"geoname_id": mmdbtype.Uint32(5375480), "names": names("Mountain View")},
			"location": mmdbtype.Map{
				"latitude": mmdbtype.Float64(37.386), "longitude": mmdbtype.Float64(-122.0838),
				"accuracy_radius": mmdbtype.Uint16(20), "time_zone": mmdbtype.String("America/Los_Angeles"),
			},
			"subdivisions": mmdbtype.Slice{mmdbtype.Map{"iso_code": mmdbtype.String("CA"), "names": names("California")}},
			"postal":       mmdbtype.Map{"code": mmdbtype.String("94035")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(15169),
				"autonomous_system_organization": mmdbtype.String("Google LLC"),
				"as_domain":                      mmdbtype.String("google.com"),
			},
			// Typed but with no prefix flags at all — the usage_type alone must drive
			// AsnType AND IsDatacenter.
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("hosting"),
				"usage_type_source": mmdbtype.String("peeringdb"),
			},
		}},
		// Clean residential-style row: ASN not in the seed, no traits.
		{"24.48.0.0/16", mmdbtype.Map{
			"country":   mmdbtype.Map{"iso_code": mmdbtype.String("CA"), "names": names("Canada")},
			"continent": mmdbtype.Map{"code": mmdbtype.String("NA"), "names": names("North America")},
			"city":      mmdbtype.Map{"names": names("Montreal")},
			"location":  mmdbtype.Map{"time_zone": mmdbtype.String("America/Toronto")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(5769),
				"autonomous_system_organization": mmdbtype.String("Videotron Telecom Ltee"),
			},
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("residential"),
				"usage_type_source": mmdbtype.String("peeringdb"),
			},
		}},
		// Hosting provider.
		{"45.32.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(20473),
				"autonomous_system_organization": mmdbtype.String("AS-VULTR"),
			},
			"traits": mmdbtype.Map{
				"is_hosting_provider": mmdbtype.Bool(true),
				"usage_type":          mmdbtype.String("hosting"),
				"usage_type_source":   mmdbtype.String("prefix_flag"),
			},
		}},
		// CDN (ASN also in the datacenter seed — CDN must win for AsnType).
		{"104.16.0.0/13", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(13335),
				"autonomous_system_organization": mmdbtype.String("Cloudflare, Inc."),
			},
			"traits": mmdbtype.Map{
				"is_cdn":            mmdbtype.Bool(true),
				"usage_type":        mmdbtype.String("hosting"),
				"usage_type_source": mmdbtype.String("prefix_flag"),
			},
		}},
		// Tor exit node.
		{"185.220.101.0/24", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("DE"), "names": names("Germany")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(208294),
				"autonomous_system_organization": mmdbtype.String("Markus Koch"),
			},
			"traits": mmdbtype.Map{"is_tor_exit_node": mmdbtype.Bool(true), "is_anonymous": mmdbtype.Bool(true)},
		}},
		// Public proxy + anonymous VPN.
		{"51.15.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("FR"), "names": names("France")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(12876),
				"autonomous_system_organization": mmdbtype.String("Online S.a.s."),
			},
			"traits": mmdbtype.Map{
				"is_public_proxy": mmdbtype.Bool(true), "is_anonymous_vpn": mmdbtype.Bool(true),
				"is_anonymous": mmdbtype.Bool(true),
			},
		}},
		// iCloud Private Relay egress (outside the embedded Apple seed CIDRs).
		{"203.0.113.0/24", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(714),
				"autonomous_system_organization": mmdbtype.String("Apple Inc."),
			},
			"traits": mmdbtype.Map{"is_relay": mmdbtype.Bool(true)},
		}},
		// Mobile carrier — the classification the previous dataset could not express.
		{"208.54.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(21928),
				"autonomous_system_organization": mmdbtype.String("T-Mobile USA, Inc."),
			},
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("mobile"),
				"usage_type_source": mmdbtype.String("asn_name"),
			},
		}},
		// Education, on an ASN that is ALSO in the datacenter seed (16509/AWS) — the
		// inferred type must outrank the seed fallback.
		{"44.224.0.0/11", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(16509),
				"autonomous_system_organization": mmdbtype.String("University Research Network"),
			},
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("education"),
				"usage_type_source": mmdbtype.String("asn_name"),
			},
		}},
		// Government.
		{"149.101.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("US"), "names": names("United States")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(3748),
				"autonomous_system_organization": mmdbtype.String("US Department of Justice"),
			},
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("government"),
				"usage_type_source": mmdbtype.String("asn_name"),
			},
		}},
		// Business.
		{"194.60.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("GB"), "names": names("United Kingdom")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(5089),
				"autonomous_system_organization": mmdbtype.String("Some Enterprise Ltd"),
			},
			"traits": mmdbtype.Map{
				"usage_type":        mmdbtype.String("business"),
				"usage_type_source": mmdbtype.String("peeringdb"),
			},
		}},
		// Untyped row: usage_type could not be inferred, so AsnType stays Unknown.
		{"196.201.0.0/16", mmdbtype.Map{
			"country": mmdbtype.Map{"iso_code": mmdbtype.String("KE"), "names": names("Kenya")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(37061),
				"autonomous_system_organization": mmdbtype.String("Unclassified Networks"),
			},
		}},
		// IPv6, ASN outside the seed.
		{"2001:db8::/32", mmdbtype.Map{
			"country":   mmdbtype.Map{"iso_code": mmdbtype.String("DE"), "names": names("Germany")},
			"continent": mmdbtype.Map{"code": mmdbtype.String("EU"), "names": names("Europe")},
			"location":  mmdbtype.Map{"time_zone": mmdbtype.String("Europe/Berlin")},
			"asn": mmdbtype.Map{
				"autonomous_system_number":       mmdbtype.Uint32(64512),
				"autonomous_system_organization": mmdbtype.String("Example Documentation AS"),
			},
		}},
	}

	tree, err := mmdbwriter.New(mmdbwriter.Options{
		DatabaseType:            "iplegence-Superior-IP",
		Description:             map[string]string{"en": "Merged free/open IP intelligence (country, ASN, traits)"},
		IPVersion:               6,
		RecordSize:              28,
		IncludeReservedNetworks: true,
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	for _, r := range rows {
		_, ipnet, err := net.ParseCIDR(r.prefix)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		if err := tree.Insert(ipnet, r.m); err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
	}
	f, err := os.Create(dest)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer f.Close()
	n, err := tree.WriteTo(f)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Printf("wrote %s (%d bytes)\n", dest, n)
}
