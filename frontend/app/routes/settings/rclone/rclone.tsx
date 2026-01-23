import { useEffect, useState, useCallback, useRef } from "react";
import { Button, Form, Spinner, Badge, Alert } from "react-bootstrap";
import type { RcloneInstance, RcloneTestResult } from "~/types/rclone";
import styles from "./rclone.module.css";

type ConnectionState = 'idle' | 'testing' | 'success' | 'error';
type SaveState = 'idle' | 'saving' | 'saved' | 'error';

export function RcloneSettings() {
    const [instances, setInstances] = useState<RcloneInstance[]>([]);
    const [loading, setLoading] = useState(true);
    const [testStates, setTestStates] = useState<Record<string, ConnectionState>>({});
    const [saveState, setSaveState] = useState<SaveState>('idle');
    const saveTimeoutRef = useRef<NodeJS.Timeout | null>(null);

    const fetchInstances = useCallback(async () => {
        try {
            const response = await fetch("/api/rclone-instances");
            if (response.ok) {
                const data = await response.json();
                setInstances(data.instances || []);
            }
        } catch (err) {
            console.error("Failed to fetch rclone instances:", err);
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => {
        fetchInstances();
    }, [fetchInstances]);

    const addInstance = useCallback(async () => {
        const formData = new FormData();
        formData.append("name", "New Rclone Instance");
        formData.append("host", "localhost");
        formData.append("port", "5572");
        formData.append("remoteName", "nzbdav:");
        formData.append("isEnabled", "true");
        formData.append("enableDirRefresh", "true");
        formData.append("enablePrefetch", "true");

        const response = await fetch("/api/rclone-instances", {
            method: "POST",
            body: formData
        });

        if (response.ok) {
            fetchInstances();
        }
    }, [fetchInstances]);

    const deleteInstance = useCallback(async (id: string) => {
        const response = await fetch(`/api/rclone-instances/${id}`, {
            method: "DELETE"
        });

        if (response.ok) {
            setInstances(prev => prev.filter(i => i.id !== id));
        }
    }, []);

    const updateInstance = useCallback(async (id: string, field: keyof RcloneInstance, value: string | boolean | number) => {
        // Update locally first for responsiveness
        setInstances(prev => prev.map(i =>
            i.id === id ? { ...i, [field]: value } : i
        ));

        // Show saving indicator
        setSaveState('saving');
        if (saveTimeoutRef.current) {
            clearTimeout(saveTimeoutRef.current);
        }

        // Then sync to server
        const instance = instances.find(i => i.id === id);
        if (!instance) return;

        const formData = new FormData();
        Object.entries({ ...instance, [field]: value }).forEach(([k, v]) => {
            if (v !== null && v !== undefined) {
                formData.append(k, String(v));
            }
        });

        try {
            const response = await fetch(`/api/rclone-instances/${id}`, {
                method: "PUT",
                body: formData
            });

            if (response.ok) {
                setSaveState('saved');
                saveTimeoutRef.current = setTimeout(() => setSaveState('idle'), 2000);
            } else {
                setSaveState('error');
                saveTimeoutRef.current = setTimeout(() => setSaveState('idle'), 3000);
            }
        } catch {
            setSaveState('error');
            saveTimeoutRef.current = setTimeout(() => setSaveState('idle'), 3000);
        }
    }, [instances]);

    const testConnection = useCallback(async (id: string) => {
        setTestStates(prev => ({ ...prev, [id]: 'testing' }));

        try {
            const response = await fetch(`/api/rclone-instances/${id}/test`, {
                method: "POST"
            });
            const data = await response.json();
            setTestStates(prev => ({
                ...prev,
                [id]: data.result?.success ? 'success' : 'error'
            }));

            // Refresh to get updated test results
            fetchInstances();
        } catch {
            setTestStates(prev => ({ ...prev, [id]: 'error' }));
        }
    }, [fetchInstances]);

    if (loading) {
        return (
            <div className={styles.container}>
                <Spinner animation="border" size="sm" /> Loading...
            </div>
        );
    }

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h5>Rclone Instances</h5>
                <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                    {saveState === 'saving' && (
                        <Badge bg="secondary"><Spinner animation="border" size="sm" /> Saving...</Badge>
                    )}
                    {saveState === 'saved' && (
                        <Badge bg="success">Saved ✓</Badge>
                    )}
                    {saveState === 'error' && (
                        <Badge bg="danger">Save failed</Badge>
                    )}
                    <Button variant="outline-secondary" size="sm" onClick={addInstance}>
                        + Add Instance
                    </Button>
                </div>
            </div>

            <Alert variant="info" className="py-2 px-3" style={{ fontSize: '0.85em' }}>
                <i className="bi bi-info-circle me-2"></i>
                Changes to Rclone instances are saved automatically. The main "Save" button below applies to other settings tabs.
            </Alert>

            <p className={styles.description}>
                Configure rclone instances for VFS cache monitoring and directory refresh operations.
                Each instance should have the RC API enabled (--rc flag).
            </p>

            {instances.length === 0 && (
                <div className={styles.emptyState}>
                    No rclone instances configured. Click "Add Instance" to add one.
                </div>
            )}

            {instances.map(instance => (
                <div key={instance.id} className={styles.instanceCard}>
                    <button
                        className={styles.closeButton}
                        onClick={() => deleteInstance(instance.id)}
                        title="Remove instance"
                    >
                        &times;
                    </button>

                    <div className={styles.formRow}>
                        <Form.Group className={styles.formGroup}>
                            <Form.Label>Name</Form.Label>
                            <Form.Control
                                type="text"
                                value={instance.name}
                                onChange={e => updateInstance(instance.id, 'name', e.target.value)}
                                placeholder="e.g., NAS01 rclone"
                            />
                        </Form.Group>
                    </div>

                    <div className={styles.formRow}>
                        <Form.Group className={styles.formGroup} style={{ flex: 2 }}>
                            <Form.Label>Host</Form.Label>
                            <Form.Control
                                type="text"
                                value={instance.host}
                                onChange={e => updateInstance(instance.id, 'host', e.target.value)}
                                placeholder="e.g., 192.168.1.100"
                            />
                        </Form.Group>
                        <Form.Group className={styles.formGroup} style={{ flex: 1 }}>
                            <Form.Label>Port</Form.Label>
                            <Form.Control
                                type="number"
                                value={instance.port}
                                onChange={e => updateInstance(instance.id, 'port', parseInt(e.target.value) || 5572)}
                            />
                        </Form.Group>
                    </div>

                    <div className={styles.formRow}>
                        <Form.Group className={styles.formGroup}>
                            <Form.Label>Remote Name</Form.Label>
                            <Form.Control
                                type="text"
                                value={instance.remoteName}
                                onChange={e => updateInstance(instance.id, 'remoteName', e.target.value)}
                                placeholder="e.g., nzbdav:"
                            />
                        </Form.Group>
                    </div>

                    <div className={styles.formRow}>
                        <Form.Group className={styles.formGroup}>
                            <Form.Label>Username (optional)</Form.Label>
                            <Form.Control
                                type="text"
                                value={instance.username || ''}
                                onChange={e => updateInstance(instance.id, 'username', e.target.value)}
                                placeholder="RC auth username"
                            />
                        </Form.Group>
                        <Form.Group className={styles.formGroup}>
                            <Form.Label>Password (optional)</Form.Label>
                            <Form.Control
                                type="password"
                                value={instance.password || ''}
                                onChange={e => updateInstance(instance.id, 'password', e.target.value)}
                                placeholder="RC auth password"
                            />
                        </Form.Group>
                    </div>

                    <div className={styles.toggleRow}>
                        <span>Enabled</span>
                        <Form.Check
                            type="switch"
                            checked={instance.isEnabled}
                            onChange={e => updateInstance(instance.id, 'isEnabled', e.target.checked)}
                        />
                    </div>

                    <div className={styles.toggleRow}>
                        <span>Enable Dir Refresh</span>
                        <Form.Check
                            type="switch"
                            checked={instance.enableDirRefresh}
                            onChange={e => updateInstance(instance.id, 'enableDirRefresh', e.target.checked)}
                        />
                    </div>

                    <div className={styles.toggleRow}>
                        <span>Enable Prefetch</span>
                        <Form.Check
                            type="switch"
                            checked={instance.enablePrefetch}
                            onChange={e => updateInstance(instance.id, 'enablePrefetch', e.target.checked)}
                        />
                    </div>

                    <div className={styles.formRow}>
                        <Form.Group className={styles.formGroup}>
                            <Form.Label>VFS Cache Path (optional)</Form.Label>
                            <Form.Control
                                type="text"
                                value={instance.vfsCachePath || ''}
                                onChange={e => updateInstance(instance.id, 'vfsCachePath', e.target.value)}
                                placeholder="/mnt/nzbdav-cache"
                            />
                            <Form.Text muted>
                                Path to rclone VFS cache directory. Used for cache status detection and cache deletion.
                                Structure: <code>{"{path}"}/vfs/{"{remote}"}/.ids/...</code>
                            </Form.Text>
                        </Form.Group>
                    </div>

                    <div className={styles.testRow}>
                        <Button
                            variant={
                                testStates[instance.id] === 'success' ? 'success' :
                                testStates[instance.id] === 'error' ? 'danger' : 'secondary'
                            }
                            size="sm"
                            onClick={() => testConnection(instance.id)}
                            disabled={testStates[instance.id] === 'testing'}
                        >
                            {testStates[instance.id] === 'testing' ? (
                                <><Spinner animation="border" size="sm" /> Testing...</>
                            ) : testStates[instance.id] === 'success' ? (
                                "Connected"
                            ) : testStates[instance.id] === 'error' ? (
                                "Failed"
                            ) : (
                                "Test Connection"
                            )}
                        </Button>

                        {instance.lastTestedAt && (
                            <span className={styles.lastTest}>
                                Last test: {new Date(instance.lastTestedAt).toLocaleString()}
                                {instance.lastTestSuccess !== null && (
                                    <Badge bg={instance.lastTestSuccess ? 'success' : 'danger'} className="ms-2">
                                        {instance.lastTestSuccess ? 'OK' : 'Failed'}
                                    </Badge>
                                )}
                            </span>
                        )}
                    </div>

                    {instance.lastTestError && (
                        <div className={styles.errorMessage}>
                            {instance.lastTestError}
                        </div>
                    )}
                </div>
            ))}
        </div>
    );
}
