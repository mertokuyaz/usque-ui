package api

import (
	"context"
	"io"
	"time"
)

// A single reader owns all device reads for the entire tunnel lifetime, not one
// reader per reconnect. Cancelling a forwarding cycle must not leave a stale
// goroutine parked in ReadPacket competing with the next connection.
type devicePacket struct {
	buffer []byte
	size   int
}

func (p *devicePacket) release(pool *NetBuffer) {
	if p != nil && p.buffer != nil {
		pool.Put(p.buffer)
		p.buffer = nil
	}
}

type deviceReadResult struct {
	packet *devicePacket
	err    error
}

type devicePacketReader struct {
	results chan deviceReadResult
}

// The device remains owned by the caller. If ReadPacket is a blocking API, the
// caller must close the device to release the one outstanding read at shutdown.
func newDevicePacketReader(ctx context.Context, device TunnelDevice, pool *NetBuffer, retry time.Duration) *devicePacketReader {
	r := &devicePacketReader{results: make(chan deviceReadResult, 1)}
	go func() {
		defer func() {
			close(r.results)
			for result := range r.results {
				result.packet.release(pool)
			}
		}()
		for ctx.Err() == nil {
			buffer := pool.Get()
			n, err := device.ReadPacket(buffer[datagramContextIDHeadroom:])
			if ctx.Err() != nil {
				pool.Put(buffer)
				return
			}
			if err == nil && (n <= 0 || n > len(buffer)-datagramContextIDHeadroom) {
				err = io.ErrUnexpectedEOF
			}
			result := deviceReadResult{err: err}
			if err != nil {
				pool.Put(buffer)
			} else {
				result.packet = &devicePacket{buffer: buffer, size: n}
			}
			select {
			case r.results <- result:
			case <-ctx.Done():
				result.packet.release(pool)
				return
			}
			if err != nil {
				// A permanently failed device must not spin a CPU when retry is zero.
				delay := retry
				if delay < 10*time.Millisecond {
					delay = 10 * time.Millisecond
				}
				if sleepCtx(ctx, delay) != nil {
					return
				}
			}
		}
	}()
	return r
}

func (r *devicePacketReader) next(ctx context.Context) (*devicePacket, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case result, ok := <-r.results:
		if !ok {
			return nil, io.EOF
		}
		return result.packet, result.err
	}
}
