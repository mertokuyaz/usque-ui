package config

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func restoreGlobals(t *testing.T) {
	t.Helper()
	previous, loaded := AppConfig, ConfigLoaded
	t.Cleanup(func() { AppConfig, ConfigLoaded = previous, loaded })
}

func TestSaveConfigUsesReceiver(t *testing.T) {
	restoreGlobals(t)
	AppConfig = Config{ID: "unrelated-global"}
	want := Config{ID: "actual-device", IPv4: "172.16.0.2"}
	path := filepath.Join(t.TempDir(), "profile.json")
	if err := want.SaveConfig(path); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var got Config
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatal(err)
	}
	if got != want {
		t.Fatalf("saved global instead of receiver: got %+v, want %+v", got, want)
	}
}

func TestLoadConfigDoesNotMergeOldProfile(t *testing.T) {
	restoreGlobals(t)
	AppConfig = Config{ID: "old-device", PrivateKey: "old-secret", EndpointH2V4: "old-endpoint"}
	path := filepath.Join(t.TempDir(), "profile.json")
	if err := os.WriteFile(path, []byte(`{"id":"new-device"}`), 0600); err != nil {
		t.Fatal(err)
	}
	if err := LoadConfig(path); err != nil {
		t.Fatal(err)
	}
	if AppConfig != (Config{ID: "new-device"}) || !ConfigLoaded {
		t.Fatalf("old profile fields leaked into new profile: %+v", AppConfig)
	}
}

func TestFailedLoadDoesNotPublishPartialProfile(t *testing.T) {
	restoreGlobals(t)
	want := Config{ID: "previous-device"}
	AppConfig, ConfigLoaded = want, true
	path := filepath.Join(t.TempDir(), "profile.json")
	if err := os.WriteFile(path, []byte(`{"id":"partial-device","ipv4":42}`), 0600); err != nil {
		t.Fatal(err)
	}
	if err := LoadConfig(path); err == nil {
		t.Fatal("invalid field type was accepted")
	}
	if AppConfig != want || ConfigLoaded {
		t.Fatalf("failed load changed the active profile or left it marked loaded: %+v, loaded=%v", AppConfig, ConfigLoaded)
	}
}

func TestLoadConfigRejectsTrailingJSON(t *testing.T) {
	restoreGlobals(t)
	for _, input := range []string{`{"id":"a"}{"id":"b"}`, `{"id":"a"}trailing garbage`, `null`} {
		t.Run(input, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "profile.json")
			if err := os.WriteFile(path, []byte(input), 0600); err != nil {
				t.Fatal(err)
			}
			if err := LoadConfig(path); err == nil {
				t.Fatal("invalid configuration document was accepted")
			}
		})
	}
}

func TestConfigKeyMethodsUseReceiver(t *testing.T) {
	restoreGlobals(t)
	AppConfig = Config{}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	privateDER, err := x509.MarshalECPrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	publicDER, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	cfg := Config{PrivateKey: base64.StdEncoding.EncodeToString(privateDER), EndpointPubKey: string(pem.EncodeToMemory(&pem.Block{Type: "PUBLIC KEY", Bytes: publicDER}))}
	private, err := cfg.GetEcPrivateKey()
	if err != nil || !key.Equal(private) {
		t.Fatalf("private key accessor ignored receiver: %v", err)
	}
	public, err := cfg.GetEcEndpointPublicKey()
	if err != nil || !key.PublicKey.Equal(public) {
		t.Fatalf("public key accessor ignored receiver: %v", err)
	}
}

func TestSaveConfigFailureLeavesNoTemporaryFiles(t *testing.T) {
	dir := t.TempDir()
	cfg := Config{ID: "device"}
	if err := cfg.SaveConfig(dir); err == nil {
		t.Fatal("saving over a directory should fail")
	}
	entries, err := os.ReadDir(filepath.Dir(dir))
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if strings.HasPrefix(entry.Name(), ".usque-config-") {
			t.Fatalf("failed save left temporary file: %s", entry.Name())
		}
	}
}

func TestSaveConfigReplacesExistingProfile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "profile.json")
	old := Config{ID: "previous-device"}
	if err := old.SaveConfig(path); err != nil {
		t.Fatal(err)
	}
	want := Config{ID: "replacement-device"}
	if err := want.SaveConfig(path); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var got Config
	if err := json.Unmarshal(data, &got); err != nil || got != want {
		t.Fatalf("replacement failed: got %+v, err=%v", got, err)
	}
}
