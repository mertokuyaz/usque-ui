package internal

import (
	"context"
	"errors"
	"net"
	"testing"
)

func TestSOCKSReadinessRequiresBoundTCPListener(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = listener.Close() }()
	ready := false
	server, err := NewSOCKS5Server(SOCKS5Config{
		Addr:    listener.Addr().String(),
		TCPOnly: true,
		DialTCP: func(context.Context, string, string) (net.Conn, error) {
			return nil, errors.New("not used")
		},
		OnReady: func() { ready = true },
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := server.Start(); err == nil || ready {
		t.Fatalf("occupied TCP port published readiness: err=%v ready=%v", err, ready)
	}
}

func TestSOCKSReadinessRequiresBoundUDPListener(t *testing.T) {
	listener, err := net.ListenUDP("udp", &net.UDPAddr{IP: net.ParseIP("127.0.0.1")})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = listener.Close() }()
	ready := false
	server, err := NewSOCKS5Server(SOCKS5Config{
		Addr: listener.LocalAddr().String(),
		DialTCP: func(context.Context, string, string) (net.Conn, error) {
			return nil, errors.New("not used")
		},
		OnReady: func() { ready = true },
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := server.Start(); err == nil || ready {
		t.Fatalf("occupied UDP port published readiness: err=%v ready=%v", err, ready)
	}
	// A failed UDP bind must also release the already-bound TCP socket.
	tcp, err := net.Listen("tcp", listener.LocalAddr().String())
	if err != nil {
		t.Fatal("TCP listener left open after failed startup:", err)
	}
	_ = tcp.Close()
}
