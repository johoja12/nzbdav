import { Button, Form, Spinner, Modal } from "react-bootstrap";
import styles from "./integrations.module.css";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useRef } from "react";

type IntegrationsSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

interface PlexServer {
    name: string;
    url: string;
    token: string;
    enabled: boolean;
}

interface EmbyServer {
    name: string;
    url: string;
    apiKey: string;
    enabled: boolean;
}

interface PlexAuthState {
    step: 'idle' | 'waiting' | 'selecting' | 'error';
    pinId?: number;
    pinCode?: string;
    authUrl?: string;
    clientId?: string;
    token?: string;
    servers?: PlexServerOption[];
    error?: string;
}

interface PlexServerOption {
    name: string;
    clientIdentifier: string;
    accessToken: string;
    connections: { uri: string; local: boolean; relay: boolean }[];
}

interface WebhookEndpoint {
    name: string;
    url: string;
    method: string;
    headers: Record<string, string>;
    events: string[];
    enabled: boolean;
}

type ConnectionState = 'idle' | 'testing' | 'success' | 'error';

export function IntegrationsSettings({ config, setNewConfig }: IntegrationsSettingsProps) {
    // Streaming Monitor settings (auto-enabled when Plex or webhooks are configured)
    const startDebounce = config["streaming-monitor.start-debounce"] || "2";
    const stopDebounce = config["streaming-monitor.stop-debounce"] || "5";

    // Plex settings
    const plexVerifyEnabled = config["plex.verify-playback"] !== "false";
    const plexServers: PlexServer[] = (() => {
        try {
            return JSON.parse(config["plex.servers"] || "[]");
        } catch {
            return [];
        }
    })();

    // Emby settings
    const embyVerifyEnabled = config["emby.verify-playback"] !== "false";
    const embyServers: EmbyServer[] = (() => {
        try {
            return JSON.parse(config["emby.servers"] || "[]");
        } catch {
            return [];
        }
    })();

    // Webhook settings
    const webhooksEnabled = config["webhooks.enabled"] === "true";
    const webhookEndpoints: WebhookEndpoint[] = (() => {
        try {
            return JSON.parse(config["webhooks.endpoints"] || "[]");
        } catch {
            return [];
        }
    })();

    // Connection test states
    const [plexTestStates, setPlexTestStates] = useState<Record<number, ConnectionState>>({});
    const [embyTestStates, setEmbyTestStates] = useState<Record<number, ConnectionState>>({});

    // Plex sign-in state
    const [plexAuth, setPlexAuth] = useState<PlexAuthState>({ step: 'idle' });
    const [showPlexModal, setShowPlexModal] = useState(false);
    const pollIntervalRef = useRef<NodeJS.Timeout | null>(null);

    // Cleanup polling on unmount
    useEffect(() => {
        return () => {
            if (pollIntervalRef.current) {
                clearInterval(pollIntervalRef.current);
            }
        };
    }, []);

    // Update config helpers - defined first as other callbacks depend on them
    const updateConfig = useCallback((key: string, value: string) => {
        setNewConfig(prev => ({ ...prev, [key]: value }));
    }, [setNewConfig]);

    const updatePlexServers = useCallback((servers: PlexServer[]) => {
        updateConfig("plex.servers", JSON.stringify(servers));
    }, [updateConfig]);

    const updateEmbyServers = useCallback((servers: EmbyServer[]) => {
        updateConfig("emby.servers", JSON.stringify(servers));
    }, [updateConfig]);

    const updateWebhooks = useCallback((endpoints: WebhookEndpoint[]) => {
        updateConfig("webhooks.endpoints", JSON.stringify(endpoints));
    }, [updateConfig]);

    // Start Plex sign-in flow
    const startPlexSignIn = useCallback(async () => {
        setShowPlexModal(true);
        setPlexAuth({ step: 'idle' });

        try {
            const response = await fetch('/api/plex-auth/pin', { method: 'POST' });
            const data = await response.json();

            if (data.error) {
                setPlexAuth({ step: 'error', error: data.error });
                return;
            }

            setPlexAuth({
                step: 'waiting',
                pinId: data.id,
                pinCode: data.code,
                authUrl: data.url,
                clientId: data.clientId
            });

            // Open Plex auth in new window
            window.open(data.url, '_blank', 'width=600,height=700');

            // Start polling for auth completion
            pollIntervalRef.current = setInterval(async () => {
                try {
                    const checkResponse = await fetch(`/api/plex-auth/pin/${data.id}?clientId=${data.clientId}`);
                    const checkData = await checkResponse.json();

                    if (checkData.authToken) {
                        // Stop polling
                        if (pollIntervalRef.current) {
                            clearInterval(pollIntervalRef.current);
                            pollIntervalRef.current = null;
                        }

                        // Fetch servers
                        const serversResponse = await fetch(`/api/plex-auth/servers?token=${checkData.authToken}&clientId=${data.clientId}`);
                        const serversData = await serversResponse.json();

                        setPlexAuth({
                            step: 'selecting',
                            token: checkData.authToken,
                            clientId: data.clientId,
                            servers: serversData.servers || []
                        });
                    }
                } catch (error) {
                    console.error('Error checking PIN:', error);
                }
            }, 2000);
        } catch (error) {
            setPlexAuth({ step: 'error', error: 'Failed to start Plex sign-in' });
        }
    }, []);

    // Select a Plex server and add it
    const selectPlexServer = useCallback(async (server: PlexServerOption, connectionUri: string) => {
        // Test the connection first
        try {
            const testResponse = await fetch('/api/plex-auth/test-connection', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url: connectionUri, token: server.accessToken })
            });
            const testData = await testResponse.json();

            if (!testData.success) {
                setPlexAuth(prev => ({ ...prev, error: `Connection failed: ${testData.error}` }));
                return;
            }
        } catch {
            // Continue anyway, user can test later
        }

        // Add the server to config
        updatePlexServers([
            ...plexServers,
            {
                name: server.name,
                url: connectionUri,
                token: server.accessToken,
                enabled: true
            }
        ]);

        // Close modal
        setShowPlexModal(false);
        setPlexAuth({ step: 'idle' });

        if (pollIntervalRef.current) {
            clearInterval(pollIntervalRef.current);
            pollIntervalRef.current = null;
        }
    }, [plexServers, updatePlexServers]);

    // Cancel Plex sign-in
    const cancelPlexSignIn = useCallback(() => {
        setShowPlexModal(false);
        setPlexAuth({ step: 'idle' });
        if (pollIntervalRef.current) {
            clearInterval(pollIntervalRef.current);
            pollIntervalRef.current = null;
        }
    }, []);

    // Plex server handlers
    const addPlexServer = useCallback(() => {
        updatePlexServers([
            ...plexServers,
            { name: "", url: "", token: "", enabled: true }
        ]);
    }, [plexServers, updatePlexServers]);

    const removePlexServer = useCallback((index: number) => {
        updatePlexServers(plexServers.filter((_, i) => i !== index));
    }, [plexServers, updatePlexServers]);

    const updatePlexServer = useCallback((index: number, field: keyof PlexServer, value: string | boolean) => {
        updatePlexServers(
            plexServers.map((server, i) =>
                i === index ? { ...server, [field]: value } : server
            )
        );
    }, [plexServers, updatePlexServers]);

    const testPlexConnection = useCallback(async (index: number, url: string, token: string) => {
        if (!url.trim() || !token.trim()) return;

        setPlexTestStates(prev => ({ ...prev, [index]: 'testing' }));

        try {
            const formData = new FormData();
            formData.append('url', url);
            formData.append('token', token);

            const response = await fetch('/api/test-plex-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();
            setPlexTestStates(prev => ({
                ...prev,
                [index]: result.status && result.connected ? 'success' : 'error'
            }));
        } catch {
            setPlexTestStates(prev => ({ ...prev, [index]: 'error' }));
        }
    }, []);

    // Emby server handlers
    const addEmbyServer = useCallback(() => {
        updateEmbyServers([
            ...embyServers,
            { name: "", url: "", apiKey: "", enabled: true }
        ]);
    }, [embyServers, updateEmbyServers]);

    const removeEmbyServer = useCallback((index: number) => {
        updateEmbyServers(embyServers.filter((_, i) => i !== index));
    }, [embyServers, updateEmbyServers]);

    const updateEmbyServer = useCallback((index: number, field: keyof EmbyServer, value: string | boolean) => {
        updateEmbyServers(
            embyServers.map((server, i) =>
                i === index ? { ...server, [field]: value } : server
            )
        );
    }, [embyServers, updateEmbyServers]);

    const testEmbyConnection = useCallback(async (index: number, url: string, apiKey: string) => {
        if (!url.trim() || !apiKey.trim()) return;

        setEmbyTestStates(prev => ({ ...prev, [index]: 'testing' }));

        try {
            const formData = new FormData();
            formData.append('url', url);
            formData.append('apiKey', apiKey);

            const response = await fetch('/api/test-emby-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();
            setEmbyTestStates(prev => ({
                ...prev,
                [index]: result.status && result.connected ? 'success' : 'error'
            }));
        } catch {
            setEmbyTestStates(prev => ({ ...prev, [index]: 'error' }));
        }
    }, []);

    // Webhook handlers
    const addWebhook = useCallback(() => {
        updateWebhooks([
            ...webhookEndpoints,
            {
                name: "",
                url: "",
                method: "POST",
                headers: {},
                events: ["streaming.started", "streaming.stopped"],
                enabled: true
            }
        ]);
    }, [webhookEndpoints, updateWebhooks]);

    const removeWebhook = useCallback((index: number) => {
        updateWebhooks(webhookEndpoints.filter((_, i) => i !== index));
    }, [webhookEndpoints, updateWebhooks]);

    const updateWebhook = useCallback((index: number, field: keyof WebhookEndpoint, value: any) => {
        updateWebhooks(
            webhookEndpoints.map((endpoint, i) =>
                i === index ? { ...endpoint, [field]: value } : endpoint
            )
        );
    }, [webhookEndpoints, updateWebhooks]);

    return (
        <div className={styles.container}>
            {/* Streaming Monitor Settings */}
            <div className={styles.section}>
                <h5>Streaming Monitor Settings</h5>

                <div className={styles.formHelp} style={{ marginBottom: '15px' }}>
                    The streaming monitor is automatically enabled when Plex servers are configured.
                    Verified playback gets <strong>priority access to the fastest Usenet provider</strong>.
                    SABnzbd auto-pause can be enabled in the SABnzbd tab.
                </div>

                <div className={styles.formGroup}>
                    <label className={styles.formLabel}>Start Debounce (seconds)</label>
                    <input
                        type="number"
                        className={styles.formInput}
                        value={startDebounce}
                        onChange={(e) => updateConfig("streaming-monitor.start-debounce", e.target.value)}
                        min="0"
                        max="30"
                    />
                    <div className={styles.formHelp}>
                        Wait this long before triggering actions when streaming starts
                    </div>
                </div>

                <div className={styles.formGroup}>
                    <label className={styles.formLabel}>Stop Debounce (seconds)</label>
                    <input
                        type="number"
                        className={styles.formInput}
                        value={stopDebounce}
                        onChange={(e) => updateConfig("streaming-monitor.stop-debounce", e.target.value)}
                        min="0"
                        max="60"
                    />
                    <div className={styles.formHelp}>
                        Wait this long before triggering actions when streaming stops
                    </div>
                </div>
            </div>

            <hr className={styles.sectionDivider} />

            {/* Plex Servers Section */}
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <h5>Plex Servers</h5>
                    <Button variant="warning" size="sm" onClick={startPlexSignIn}>
                        Sign in with Plex
                    </Button>
                </div>

                <div className={styles.toggleRow}>
                    <span className={styles.toggleLabel}>Verify playback via Plex API</span>
                    <Form.Check
                        type="switch"
                        checked={plexVerifyEnabled}
                        onChange={(e) => updateConfig("plex.verify-playback", e.target.checked ? "true" : "false")}
                    />
                </div>

                <div className={styles.formHelp}>
                    When enabled, NZBDav verifies real Plex playback sessions. Verified playback streams get
                    <strong> priority access to the fastest Usenet provider</strong>, while background activity
                    (intro detection, thumbnails) uses secondary providers. This also controls SABnzbd auto-pause behavior.
                </div>

                {plexServers.map((server, index) => (
                    <div key={index} className={styles.instanceCard}>
                        <button
                            className={styles.closeButton}
                            onClick={() => removePlexServer(index)}
                            title="Remove server"
                        >
                            &times;
                        </button>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="Server Name (e.g., NAS01 Plex)"
                                value={server.name}
                                onChange={(e) => updatePlexServer(index, 'name', e.target.value)}
                            />
                        </div>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="URL (e.g., http://192.168.1.100:32400)"
                                value={server.url}
                                onChange={(e) => updatePlexServer(index, 'url', e.target.value)}
                            />
                        </div>

                        <div className={styles.inputGroup}>
                            <input
                                type="password"
                                className={`${styles.formInput} ${styles.inputGroupInput}`}
                                placeholder="Plex Token"
                                value={server.token}
                                onChange={(e) => updatePlexServer(index, 'token', e.target.value)}
                            />
                            <Button
                                variant={
                                    plexTestStates[index] === 'success' ? 'success' :
                                    plexTestStates[index] === 'error' ? 'danger' : 'secondary'
                                }
                                onClick={() => testPlexConnection(index, server.url, server.token)}
                                disabled={plexTestStates[index] === 'testing'}
                                className={styles.testButton}
                            >
                                {plexTestStates[index] === 'testing' ? (
                                    <Spinner animation="border" size="sm" />
                                ) : plexTestStates[index] === 'success' ? (
                                    "Connected"
                                ) : plexTestStates[index] === 'error' ? (
                                    "Failed"
                                ) : (
                                    "Test"
                                )}
                            </Button>
                        </div>

                        <div className={styles.toggleRow}>
                            <span className={styles.toggleLabel}>Enabled</span>
                            <Form.Check
                                type="switch"
                                checked={server.enabled}
                                onChange={(e) => updatePlexServer(index, 'enabled', e.target.checked)}
                            />
                        </div>
                    </div>
                ))}

                {plexServers.length === 0 && (
                    <div className={styles.formHelp}>
                        No Plex servers configured. Add a server to enable playback verification.
                    </div>
                )}
            </div>

            <hr className={styles.sectionDivider} />

            {/* Emby Servers Section */}
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <h5>Emby Servers</h5>
                    <Button variant="outline-secondary" size="sm" onClick={addEmbyServer}>
                        + Add Server
                    </Button>
                </div>

                <div className={styles.toggleRow}>
                    <span className={styles.toggleLabel}>Verify playback via Emby API</span>
                    <Form.Check
                        type="switch"
                        checked={embyVerifyEnabled}
                        onChange={(e) => updateConfig("emby.verify-playback", e.target.checked ? "true" : "false")}
                    />
                </div>

                <div className={styles.formHelp}>
                    When enabled, NZBDav will check Emby to verify real user playback before pausing SABnzbd.
                </div>

                {embyServers.map((server, index) => (
                    <div key={index} className={styles.instanceCard}>
                        <button
                            className={styles.closeButton}
                            onClick={() => removeEmbyServer(index)}
                            title="Remove server"
                        >
                            &times;
                        </button>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="Server Name (e.g., NAS02 Emby)"
                                value={server.name}
                                onChange={(e) => updateEmbyServer(index, 'name', e.target.value)}
                            />
                        </div>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="URL (e.g., http://192.168.1.100:8096)"
                                value={server.url}
                                onChange={(e) => updateEmbyServer(index, 'url', e.target.value)}
                            />
                        </div>

                        <div className={styles.inputGroup}>
                            <input
                                type="password"
                                className={`${styles.formInput} ${styles.inputGroupInput}`}
                                placeholder="Emby API Key"
                                value={server.apiKey}
                                onChange={(e) => updateEmbyServer(index, 'apiKey', e.target.value)}
                            />
                            <Button
                                variant={
                                    embyTestStates[index] === 'success' ? 'success' :
                                    embyTestStates[index] === 'error' ? 'danger' : 'secondary'
                                }
                                onClick={() => testEmbyConnection(index, server.url, server.apiKey)}
                                disabled={embyTestStates[index] === 'testing'}
                                className={styles.testButton}
                            >
                                {embyTestStates[index] === 'testing' ? (
                                    <Spinner animation="border" size="sm" />
                                ) : embyTestStates[index] === 'success' ? (
                                    "Connected"
                                ) : embyTestStates[index] === 'error' ? (
                                    "Failed"
                                ) : (
                                    "Test"
                                )}
                            </Button>
                        </div>

                        <div className={styles.toggleRow}>
                            <span className={styles.toggleLabel}>Enabled</span>
                            <Form.Check
                                type="switch"
                                checked={server.enabled}
                                onChange={(e) => updateEmbyServer(index, 'enabled', e.target.checked)}
                            />
                        </div>
                    </div>
                ))}

                {embyServers.length === 0 && (
                    <div className={styles.formHelp}>
                        No Emby servers configured. Add a server to enable playback verification.
                    </div>
                )}
            </div>

            <hr className={styles.sectionDivider} />

            {/* Webhooks Section */}
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <h5>Webhooks</h5>
                    <Button variant="outline-secondary" size="sm" onClick={addWebhook}>
                        + Add Webhook
                    </Button>
                </div>

                <div className={styles.toggleRow}>
                    <span className={styles.toggleLabel}>Enable webhooks</span>
                    <Form.Check
                        type="switch"
                        checked={webhooksEnabled}
                        onChange={(e) => updateConfig("webhooks.enabled", e.target.checked ? "true" : "false")}
                    />
                </div>

                <div className={styles.formHelp}>
                    Fire webhooks when streaming events occur.
                </div>

                {webhookEndpoints.map((endpoint, index) => (
                    <div key={index} className={styles.instanceCard}>
                        <button
                            className={styles.closeButton}
                            onClick={() => removeWebhook(index)}
                            title="Remove webhook"
                        >
                            &times;
                        </button>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="Webhook Name"
                                value={endpoint.name}
                                onChange={(e) => updateWebhook(index, 'name', e.target.value)}
                            />
                        </div>

                        <div className={styles.formGroup}>
                            <input
                                type="text"
                                className={styles.formInput}
                                placeholder="URL (e.g., https://example.com/webhook)"
                                value={endpoint.url}
                                onChange={(e) => updateWebhook(index, 'url', e.target.value)}
                            />
                        </div>

                        <div className={styles.formGroup}>
                            <label className={styles.formLabel}>Method</label>
                            <select
                                className={styles.formSelect}
                                value={endpoint.method}
                                onChange={(e) => updateWebhook(index, 'method', e.target.value)}
                            >
                                <option value="POST">POST</option>
                                <option value="GET">GET</option>
                                <option value="PUT">PUT</option>
                            </select>
                        </div>

                        <div className={styles.formGroup}>
                            <label className={styles.formLabel}>Events</label>
                            <div className={styles.webhookEvents}>
                                {["streaming.started", "streaming.stopped"].map((event) => (
                                    <Form.Check
                                        key={event}
                                        type="checkbox"
                                        label={event}
                                        checked={endpoint.events.includes(event)}
                                        onChange={(e) => {
                                            const newEvents = e.target.checked
                                                ? [...endpoint.events, event]
                                                : endpoint.events.filter(ev => ev !== event);
                                            updateWebhook(index, 'events', newEvents);
                                        }}
                                    />
                                ))}
                            </div>
                        </div>

                        <div className={styles.toggleRow}>
                            <span className={styles.toggleLabel}>Enabled</span>
                            <Form.Check
                                type="switch"
                                checked={endpoint.enabled}
                                onChange={(e) => updateWebhook(index, 'enabled', e.target.checked)}
                            />
                        </div>
                    </div>
                ))}

                {webhookEndpoints.length === 0 && (
                    <div className={styles.formHelp}>
                        No webhooks configured. Add a webhook to receive streaming event notifications.
                    </div>
                )}
            </div>

            {/* Plex Sign-In Modal */}
            <Modal show={showPlexModal} onHide={cancelPlexSignIn} centered>
                <Modal.Header closeButton>
                    <Modal.Title>Sign in with Plex</Modal.Title>
                </Modal.Header>
                <Modal.Body>
                    {plexAuth.step === 'idle' && (
                        <div className="text-center py-4">
                            <Spinner animation="border" />
                            <p className="mt-3">Starting Plex sign-in...</p>
                        </div>
                    )}

                    {plexAuth.step === 'waiting' && (
                        <div className="text-center py-4">
                            <Spinner animation="border" variant="warning" />
                            <p className="mt-3">
                                A new window should have opened for Plex sign-in.
                                <br />
                                Waiting for authorization...
                            </p>
                            <p className="text-muted small">
                                If the window didn&apos;t open,{' '}
                                <a href={plexAuth.authUrl} target="_blank" rel="noopener noreferrer">
                                    click here
                                </a>
                            </p>
                        </div>
                    )}

                    {plexAuth.step === 'selecting' && (
                        <div>
                            <p>Select a server to add:</p>
                            {plexAuth.servers && plexAuth.servers.length > 0 ? (
                                <div className={styles.serverList}>
                                    {plexAuth.servers.map((server) => (
                                        <div key={server.clientIdentifier} className={styles.serverOption}>
                                            <div className={styles.serverName}>{server.name}</div>
                                            <div className={styles.serverConnections}>
                                                {server.connections.map((conn, idx) => (
                                                    <Button
                                                        key={idx}
                                                        variant={conn.relay ? 'outline-secondary' : conn.local ? 'outline-success' : 'outline-primary'}
                                                        size="sm"
                                                        className="me-2 mb-2"
                                                        onClick={() => selectPlexServer(server, conn.uri)}
                                                    >
                                                        {conn.local ? 'Local' : conn.relay ? 'Relay' : 'Remote'}
                                                        <span className="ms-1 text-muted small">
                                                            {new URL(conn.uri).host}
                                                        </span>
                                                    </Button>
                                                ))}
                                            </div>
                                        </div>
                                    ))}
                                </div>
                            ) : (
                                <p className="text-muted">No servers found on your Plex account.</p>
                            )}
                            {plexAuth.error && (
                                <div className="alert alert-danger mt-3">{plexAuth.error}</div>
                            )}
                        </div>
                    )}

                    {plexAuth.step === 'error' && (
                        <div className="alert alert-danger">
                            {plexAuth.error || 'An error occurred during sign-in.'}
                        </div>
                    )}
                </Modal.Body>
                <Modal.Footer>
                    <Button variant="secondary" onClick={cancelPlexSignIn}>
                        Cancel
                    </Button>
                    {plexAuth.step === 'error' && (
                        <Button variant="warning" onClick={startPlexSignIn}>
                            Try Again
                        </Button>
                    )}
                </Modal.Footer>
            </Modal>
        </div>
    );
}

export function isIntegrationsSettingsUpdated(
    config: Record<string, string>,
    newConfig: Record<string, string>
): boolean {
    const keys = [
        "streaming-monitor.start-debounce",
        "streaming-monitor.stop-debounce",
        "plex.verify-playback",
        "plex.servers",
        "emby.verify-playback",
        "emby.servers",
        "webhooks.enabled",
        "webhooks.endpoints"
    ];

    return keys.some(key => config[key] !== newConfig[key]);
}

export function isIntegrationsSettingsValid(config: Record<string, string>): boolean {
    // Validation is now simpler with multi-server support
    return true;
}
