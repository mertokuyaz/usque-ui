package api

import (
	"encoding/json"
	"io"
	"sync"
)

// TunnelStatus is a machine-readable lifecycle event. Connected means the
// MASQUE handshake succeeded, not that the caller configured routing or DNS.
type TunnelStatus struct {
	State     string `json:"state"`
	Interface string `json:"interface,omitempty"`
	Endpoint  string `json:"endpoint,omitempty"`
}

// NewStatusReporter writes one JSON object per line. Logs remain on stderr.
// Reports are serialized, including reports from different goroutines.
func NewStatusReporter(w io.Writer) func(TunnelStatus) {
	var mu sync.Mutex
	encoder := json.NewEncoder(w)
	return func(status TunnelStatus) {
		mu.Lock()
		defer mu.Unlock()
		_ = encoder.Encode(status)
	}
}
