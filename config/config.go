package config

import (
	"crypto/ecdsa"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"os"
	"path/filepath"
)

// Config represents the application configuration structure, containing essential details such as keys, endpoints, and access tokens.
type Config struct {
	PrivateKey     string `json:"private_key"`      // Base64-encoded ECDSA private key
	EndpointV4     string `json:"endpoint_v4"`      // IPv4 address of the endpoint
	EndpointV6     string `json:"endpoint_v6"`      // IPv6 address of the endpoint
	EndpointH2V4   string `json:"endpoint_h2_v4"`   // IPv4 address used in HTTP/2 mode
	EndpointH2V6   string `json:"endpoint_h2_v6"`   // IPv6 address used in HTTP/2 mode
	EndpointPubKey string `json:"endpoint_pub_key"` // PEM-encoded ECDSA public key of the endpoint to verify against
	ID             string `json:"id"`               // Device unique identifier
	AccessToken    string `json:"access_token"`     // Authentication token for API access
	IPv4           string `json:"ipv4"`             // Assigned IPv4 address
	IPv6           string `json:"ipv6"`             // Assigned IPv6 address
}

// AppConfig holds the global application configuration.
var AppConfig Config

// ConfigLoaded indicates whether the configuration has been successfully loaded.
var ConfigLoaded bool

// LoadConfig loads the application configuration from a JSON file.
//
// Parameters:
//   - configPath: string - The path to the configuration JSON file.
//
// Returns:
//   - error: An error if the configuration file cannot be loaded or parsed.
func LoadConfig(configPath string) error {
	ConfigLoaded = false
	file, err := os.Open(configPath)
	if err != nil {
		return fmt.Errorf("failed to open config file: %v", err)
	}
	defer func() { _ = file.Close() }()

	decoder := json.NewDecoder(file)
	var candidate *Config
	if err := decoder.Decode(&candidate); err != nil {
		return fmt.Errorf("failed to decode config file: %v", err)
	}
	if candidate == nil {
		return fmt.Errorf("config file must contain a JSON object")
	}
	var trailing json.RawMessage
	if err := decoder.Decode(&trailing); err != io.EOF {
		return fmt.Errorf("config file must contain exactly one JSON object")
	}

	AppConfig = *candidate
	ConfigLoaded = true

	return nil
}

// SaveConfig writes this configuration to a prettified JSON file. Write to a
// temporary file first so a failed write never truncates an existing profile.
//
// Parameters:
//   - configPath: string - The path to save the configuration JSON file.
//
// Returns:
//   - error: An error if the configuration file cannot be written.
func (cfg *Config) SaveConfig(configPath string) error {
	if cfg == nil {
		return fmt.Errorf("cannot save a nil configuration")
	}
	file, err := os.CreateTemp(filepath.Dir(configPath), ".usque-config-*")
	if err != nil {
		return fmt.Errorf("failed to create config file: %v", err)
	}
	defer func() {
		_ = file.Close()
		_ = os.Remove(file.Name())
	}()

	encoder := json.NewEncoder(file)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(cfg); err != nil {
		return fmt.Errorf("failed to encode config file: %v", err)
	}
	if err := file.Sync(); err != nil {
		return fmt.Errorf("failed to flush config file: %v", err)
	}
	if err := file.Close(); err != nil {
		return fmt.Errorf("failed to close config file: %v", err)
	}
	if err := os.Rename(file.Name(), configPath); err != nil {
		return fmt.Errorf("failed to replace config file: %v", err)
	}

	return nil
}

// GetEcPrivateKey retrieves the ECDSA private key from the stored Base64-encoded string.
//
// Returns:
//   - *ecdsa.PrivateKey: The parsed ECDSA private key.
//   - error: An error if decoding or parsing the private key fails.
func (cfg *Config) GetEcPrivateKey() (*ecdsa.PrivateKey, error) {
	if cfg == nil {
		return nil, fmt.Errorf("configuration is nil")
	}
	privKeyB64, err := base64.StdEncoding.DecodeString(cfg.PrivateKey)
	if err != nil {
		return nil, fmt.Errorf("failed to decode private key: %v", err)
	}

	privKey, err := x509.ParseECPrivateKey(privKeyB64)
	if err != nil {
		return nil, fmt.Errorf("failed to parse private key: %v", err)
	}

	return privKey, nil
}

// GetEcEndpointPublicKey retrieves the ECDSA public key from the stored PEM-encoded string.
//
// Returns:
//   - *ecdsa.PublicKey: The parsed ECDSA public key.
//   - error: An error if decoding or parsing the public key fails.
func (cfg *Config) GetEcEndpointPublicKey() (*ecdsa.PublicKey, error) {
	if cfg == nil {
		return nil, fmt.Errorf("configuration is nil")
	}
	endpointPubKeyB64, _ := pem.Decode([]byte(cfg.EndpointPubKey))
	if endpointPubKeyB64 == nil {
		return nil, fmt.Errorf("failed to decode endpoint public key")
	}

	pubKey, err := x509.ParsePKIXPublicKey(endpointPubKeyB64.Bytes)
	if err != nil {
		return nil, fmt.Errorf("failed to parse public key: %v", err)
	}

	ecPubKey, ok := pubKey.(*ecdsa.PublicKey)
	if !ok {
		return nil, fmt.Errorf("failed to assert public key as ECDSA")
	}

	return ecPubKey, nil
}
