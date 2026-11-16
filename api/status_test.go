package api

import (
	"bytes"
	"encoding/json"
	"net"
	"sync"
	"testing"
)

func TestStatusReporter(t *testing.T) {
	var output bytes.Buffer
	report := NewStatusReporter(&output)
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			report(TunnelStatus{State: "ready", Interface: "Usque WARP"})
		}()
	}
	wg.Wait()
	decoder := json.NewDecoder(&output)
	for i := 0; i < 20; i++ {
		var status TunnelStatus
		if err := decoder.Decode(&status); err != nil {
			t.Fatal(err)
		}
		if status.State != "ready" || status.Interface != "Usque WARP" {
			t.Fatalf("unexpected status: %+v", status)
		}
	}
}

func TestReportStatus(t *testing.T) {
	cfg := MaintainTunnelConfig{Endpoint: &net.UDPAddr{IP: net.ParseIP("162.159.198.1"), Port: 443}}
	cfg.reportStatus("connecting") // Optional callback must be safe.
	cfg.OnStatus = func(status TunnelStatus) {
		if status.State != "connected" || status.Endpoint != "162.159.198.1:443" {
			t.Fatalf("unexpected status: %+v", status)
		}
	}
	cfg.reportStatus("connected")
}
