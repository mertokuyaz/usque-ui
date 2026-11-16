package api

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type blockingTestDevice struct {
	packets chan []byte
	started chan struct{}
	closed  chan struct{}
	once    sync.Once
	active  atomic.Int32
	maximum atomic.Int32
}

func newBlockingTestDevice() *blockingTestDevice {
	return &blockingTestDevice{packets: make(chan []byte), started: make(chan struct{}), closed: make(chan struct{})}
}

func (d *blockingTestDevice) ReadPacket(buffer []byte) (int, error) {
	active := d.active.Add(1)
	defer d.active.Add(-1)
	for {
		previous := d.maximum.Load()
		if active <= previous || d.maximum.CompareAndSwap(previous, active) {
			break
		}
	}
	d.once.Do(func() { close(d.started) })
	select {
	case <-d.closed:
		return 0, io.EOF
	case packet := <-d.packets:
		return copy(buffer, packet), nil
	}
}

func (*blockingTestDevice) WritePacket([]byte) error { return nil }

func TestCancelledCycleDoesNotStrandADeviceReader(t *testing.T) {
	device := newBlockingTestDevice()
	defer close(device.closed)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	pool := NewNetBuffer(1281)
	reader := newDevicePacketReader(ctx, device, pool, time.Millisecond)
	<-device.started

	cycle, cancelCycle := context.WithCancel(ctx)
	finished := make(chan error, 1)
	go func() { _, err := reader.next(cycle); finished <- err }()
	cancelCycle()
	select {
	case err := <-finished:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("unexpected cancellation error: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("cancelled forwarding cycle remained stuck in device ReadPacket")
	}

	want := []byte{0x45, 0, 0, 20}
	go func() {
		select {
		case device.packets <- want:
		case <-ctx.Done():
		}
	}()
	deadline, stop := context.WithTimeout(ctx, time.Second)
	defer stop()
	packet, err := reader.next(deadline)
	if err != nil {
		t.Fatal(err)
	}
	defer packet.release(pool)
	if !bytes.Equal(packet.buffer[datagramContextIDHeadroom:datagramContextIDHeadroom+packet.size], want) {
		t.Fatal("next connection lost or changed the first pending packet")
	}
	if device.maximum.Load() != 1 {
		t.Fatalf("overlapping TUN readers: %d", device.maximum.Load())
	}
}

func TestIdleTunnelCancellationReturnsWithoutDeviceActivity(t *testing.T) {
	device := newBlockingTestDevice()
	defer close(device.closed)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	finished := make(chan struct{})
	go func() {
		MaintainTunnel(ctx, MaintainTunnelConfig{
			Endpoint: &net.UDPAddr{IP: net.ParseIP("192.0.2.1"), Port: 443},
			Device:   device, MTU: 1280, ReconnectDelay: time.Millisecond,
		})
		close(finished)
	}()
	<-device.started
	cancel() // No packet and no network handshake are necessary for shutdown.
	select {
	case <-finished:
	case <-time.After(time.Second):
		t.Fatal("idle tunnel ignored context cancellation")
	}
}

func TestNetBufferNormalizesReturnedSlices(t *testing.T) {
	pool := NewNetBuffer(1281)
	buffer := pool.Get()
	pool.Put(buffer[:0])
	if got := pool.Get(); len(got) != 1281 {
		t.Fatalf("pool returned a shortened packet buffer: length %d", len(got))
	}
}
