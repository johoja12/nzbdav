import { useEffect, useState, useCallback, useRef } from "react";
import { Button, Form, Spinner, Badge, Alert, Collapse } from "react-bootstrap";
import type { RcloneInstance, RcloneTestResult } from "~/types/rclone";
import styles from "./rclone.module.css";

type ConnectionState = 'idle' | 'testing' | 'success' | 'error';
type SaveState = 'idle' | 'saving' | 'saved' | 'error';
type MigrationState = 'idle' | 'migrating' | 'success' | 'error';

interface ShardRecommendation {
    shardIndex: number;
    recommendedPrefixes: string;
    totalShards: number;
    currentPrefixes?: string;
    isShardEnabled: boolean;
}

interface MigrationStatusData {
    isRunning: boolean;
    currentPhase: string;
    currentFile: string | null;
    filesMoved: number;
    bytesMoved: number;
    totalFilesEstimate: number;
    totalBytesEstimate: number;
    sourceInstance: string | null;
    targetInstance: string | null;
    startedAt: string | null;
    recentErrors: string[];
    progressPercent: number;
}

export function RcloneSettings() {
    const [instances, setInstances] = useState<RcloneInstance[]>([]);
    const [loading, setLoading] = useState(true);
    const [testStates, setTestStates] = useState<Record<string, ConnectionState>>({});
    const [saveState, setSaveState] = useState<SaveState>('idle');
    const [migrationStates, setMigrationStates] = useState<Record<string, MigrationState>>({});
    const [migrationResults, setMigrationResults] = useState<Record<string, { message?: string; error?: string }>>({});
    const [recommendations, setRecommendations] = useState<Record<string, ShardRecommendation>>({});
    const [expandedInstances, setExpandedInstances] = useState<Record<string, boolean>>({});
    const [migrationStatus, setMigrationStatus] = useState<MigrationStatusData | null>(null);
    const [globalMigrating, setGlobalMigrating] = useState(false);
    const saveTimeoutRef = useRef<NodeJS.Timeout | null>(null);
    const statusPollRef = useRef<NodeJS.Timeout | null>(null);

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

    const fetchRecommendations = useCallback(async () => {
        try {
            const response = await fetch("/api/rclone-instances/shard-recommendations");
            if (response.ok) {
                const data = await response.json();
                setRecommendations(data.recommendations || {});
            }
        } catch (err) {
            console.error("Failed to fetch shard recommendations:", err);
        }
    }, []);

    const fetchMigrationStatus = useCallback(async () => {
        try {
            const response = await fetch("/api/rclone-instances/cache-migration/status");
            if (response.ok) {
                const data = await response.json();
                setMigrationStatus(data);
                return data.isRunning;
            }
        } catch (err) {
            console.error("Failed to fetch migration status:", err);
        }
        return false;
    }, []);

    const startStatusPolling = useCallback(() => {
        if (statusPollRef.current) return;

        const poll = async () => {
            const isRunning = await fetchMigrationStatus();
            if (isRunning) {
                statusPollRef.current = setTimeout(poll, 1000);
            } else {
                statusPollRef.current = null;
                setGlobalMigrating(false);
            }
        };

        poll();
    }, [fetchMigrationStatus]);

    const stopStatusPolling = useCallback(() => {
        if (statusPollRef.current) {
            clearTimeout(statusPollRef.current);
            statusPollRef.current = null;
        }
    }, []);

    useEffect(() => {
        fetchInstances();
    }, [fetchInstances]);

    // Fetch recommendations when instances change
    useEffect(() => {
        if (instances.length > 0) {
            fetchRecommendations();
        }
    }, [instances.length, fetchRecommendations]);

    const toggleExpanded = useCallback((id: string) => {
        setExpandedInstances(prev => ({ ...prev, [id]: !prev[id] }));
    }, []);

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
            const data = await response.json();
            // Auto-expand the new instance
            if (data.instance?.id) {
                setExpandedInstances(prev => ({ ...prev, [data.instance.id]: true }));
            }
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

    const applyRecommendation = useCallback(async (id: string) => {
        try {
            const response = await fetch(`/api/rclone-instances/${id}/apply-shard-recommendation`, {
                method: "POST"
            });
            if (response.ok) {
                fetchInstances();
                fetchRecommendations();
            }
        } catch (err) {
            console.error("Failed to apply recommendation:", err);
        }
    }, [fetchInstances, fetchRecommendations]);

    const migrateCache = useCallback(async (id: string) => {
        setMigrationStates(prev => ({ ...prev, [id]: 'migrating' }));
        setMigrationResults(prev => ({ ...prev, [id]: {} }));
        setGlobalMigrating(true);
        startStatusPolling();

        try {
            const response = await fetch(`/api/rclone-instances/${id}/migrate-cache`, {
                method: "POST"
            });
            const data = await response.json();

            if (response.ok) {
                setMigrationStates(prev => ({ ...prev, [id]: 'success' }));
                setMigrationResults(prev => ({
                    ...prev,
                    [id]: { message: data.message || `Moved ${data.filesMoved} files` }
                }));
            } else {
                setMigrationStates(prev => ({ ...prev, [id]: 'error' }));
                setMigrationResults(prev => ({
                    ...prev,
                    [id]: { error: data.error || 'Migration failed' }
                }));
            }
        } catch (err) {
            setMigrationStates(prev => ({ ...prev, [id]: 'error' }));
            setMigrationResults(prev => ({
                ...prev,
                [id]: { error: 'Migration request failed' }
            }));
        }
    }, [startStatusPolling]);

    // Cleanup polling on unmount
    useEffect(() => {
        return () => stopStatusPolling();
    }, [stopStatusPolling]);

    const getStatusBadge = (instance: RcloneInstance) => {
        if (!instance.isEnabled) {
            return <Badge bg="secondary">Disabled</Badge>;
        }
        if (instance.lastTestSuccess === true) {
            return <Badge bg="success">Connected</Badge>;
        }
        if (instance.lastTestSuccess === false) {
            return <Badge bg="danger">Failed</Badge>;
        }
        return <Badge bg="warning">Not Tested</Badge>;
    };

    const formatBytes = (bytes: number): string => {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return `${(bytes / Math.pow(k, i)).toFixed(2)} ${sizes[i]}`;
    };

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

            <p className={styles.description}>
                Configure rclone instances for VFS cache monitoring and directory refresh operations.
            </p>

            {/* Migration Status Panel */}
            {(globalMigrating || migrationStatus?.isRunning) && migrationStatus && (
                <Alert variant="info" className={styles.migrationStatusPanel}>
                    <div className={styles.migrationStatusHeader}>
                        <strong><Spinner animation="border" size="sm" /> Cache Migration in Progress</strong>
                        <Badge bg="primary">{migrationStatus.progressPercent}%</Badge>
                    </div>
                    <div className={styles.migrationProgress}>
                        <div
                            className={styles.migrationProgressBar}
                            style={{ width: `${migrationStatus.progressPercent}%` }}
                        />
                    </div>
                    <div className={styles.migrationDetails}>
                        <span><strong>Phase:</strong> {migrationStatus.currentPhase}</span>
                        {migrationStatus.sourceInstance && migrationStatus.targetInstance && (
                            <span><strong>Transfer:</strong> {migrationStatus.sourceInstance} → {migrationStatus.targetInstance}</span>
                        )}
                        <span>
                            <strong>Progress:</strong> {migrationStatus.filesMoved}/{migrationStatus.totalFilesEstimate} files
                            ({formatBytes(migrationStatus.bytesMoved)} / {formatBytes(migrationStatus.totalBytesEstimate)})
                        </span>
                        {migrationStatus.currentFile && (
                            <span className={styles.currentFile}><strong>Current:</strong> {migrationStatus.currentFile}</span>
                        )}
                    </div>
                    {migrationStatus.recentErrors.length > 0 && (
                        <div className={styles.migrationErrors}>
                            <strong>Recent Errors:</strong>
                            {migrationStatus.recentErrors.slice(-3).map((err, i) => (
                                <div key={i} className={styles.migrationError}>{err}</div>
                            ))}
                        </div>
                    )}
                </Alert>
            )}

            {instances.length === 0 && (
                <div className={styles.emptyState}>
                    No rclone instances configured. Click "Add Instance" to add one.
                </div>
            )}

            {instances.map(instance => {
                const isExpanded = expandedInstances[instance.id] ?? false;

                return (
                    <div key={instance.id} className={styles.instanceCard}>
                        {/* Compact Header - Always Visible */}
                        <div
                            className={styles.cardHeader}
                            onClick={() => toggleExpanded(instance.id)}
                        >
                            <div className={styles.cardHeaderLeft}>
                                <span className={styles.expandIcon}>
                                    {isExpanded ? '▼' : '▶'}
                                </span>
                                <span className={styles.instanceName}>{instance.name}</span>
                                {getStatusBadge(instance)}
                                {instance.isShardEnabled && instance.shardPrefixes && (
                                    <Badge bg="info" className={styles.shardBadge}>
                                        Shard: {instance.shardPrefixes}
                                    </Badge>
                                )}
                            </div>
                            <div className={styles.cardHeaderRight}>
                                <span className={styles.hostInfo}>
                                    {instance.host}:{instance.port}
                                </span>
                                <Button
                                    variant={
                                        testStates[instance.id] === 'success' ? 'outline-success' :
                                        testStates[instance.id] === 'error' ? 'outline-danger' : 'outline-secondary'
                                    }
                                    size="sm"
                                    onClick={(e) => { e.stopPropagation(); testConnection(instance.id); }}
                                    disabled={testStates[instance.id] === 'testing'}
                                    className={styles.testButton}
                                >
                                    {testStates[instance.id] === 'testing' ? (
                                        <Spinner animation="border" size="sm" />
                                    ) : (
                                        "Test"
                                    )}
                                </Button>
                                <button
                                    className={styles.deleteButton}
                                    onClick={(e) => { e.stopPropagation(); deleteInstance(instance.id); }}
                                    title="Remove instance"
                                >
                                    ×
                                </button>
                            </div>
                        </div>

                        {/* Expandable Content */}
                        <Collapse in={isExpanded}>
                            <div className={styles.cardBody}>
                                {/* Connection Settings */}
                                <div className={styles.settingsSection}>
                                    <div className={styles.sectionTitle}>Connection</div>
                                    <div className={styles.compactFormRow}>
                                        <Form.Group className={styles.formGroupCompact}>
                                            <Form.Label>Name</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="text"
                                                value={instance.name}
                                                onChange={e => updateInstance(instance.id, 'name', e.target.value)}
                                            />
                                        </Form.Group>
                                        <Form.Group className={styles.formGroupCompact} style={{ flex: 2 }}>
                                            <Form.Label>Host</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="text"
                                                value={instance.host}
                                                onChange={e => updateInstance(instance.id, 'host', e.target.value)}
                                            />
                                        </Form.Group>
                                        <Form.Group className={styles.formGroupCompact} style={{ flex: 0.5 }}>
                                            <Form.Label>Port</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="number"
                                                value={instance.port}
                                                onChange={e => updateInstance(instance.id, 'port', parseInt(e.target.value) || 5572)}
                                            />
                                        </Form.Group>
                                    </div>

                                    <div className={styles.compactFormRow}>
                                        <Form.Group className={styles.formGroupCompact}>
                                            <Form.Label>Remote Name</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="text"
                                                value={instance.remoteName}
                                                onChange={e => updateInstance(instance.id, 'remoteName', e.target.value)}
                                            />
                                        </Form.Group>
                                        <Form.Group className={styles.formGroupCompact}>
                                            <Form.Label>Username</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="text"
                                                value={instance.username || ''}
                                                onChange={e => updateInstance(instance.id, 'username', e.target.value)}
                                                placeholder="optional"
                                            />
                                        </Form.Group>
                                        <Form.Group className={styles.formGroupCompact}>
                                            <Form.Label>Password</Form.Label>
                                            <Form.Control
                                                size="sm"
                                                type="password"
                                                value={instance.password || ''}
                                                onChange={e => updateInstance(instance.id, 'password', e.target.value)}
                                                placeholder="optional"
                                            />
                                        </Form.Group>
                                    </div>
                                </div>

                                {/* Features */}
                                <div className={styles.settingsSection}>
                                    <div className={styles.sectionTitle}>Features</div>
                                    <div className={styles.toggleGrid}>
                                        <label className={styles.toggleItem}>
                                            <Form.Check
                                                type="switch"
                                                checked={instance.isEnabled}
                                                onChange={e => updateInstance(instance.id, 'isEnabled', e.target.checked)}
                                            />
                                            <span>Enabled</span>
                                        </label>
                                        <label className={styles.toggleItem}>
                                            <Form.Check
                                                type="switch"
                                                checked={instance.enableDirRefresh}
                                                onChange={e => updateInstance(instance.id, 'enableDirRefresh', e.target.checked)}
                                            />
                                            <span>Dir Refresh</span>
                                        </label>
                                        <label className={styles.toggleItem}>
                                            <Form.Check
                                                type="switch"
                                                checked={instance.enablePrefetch}
                                                onChange={e => updateInstance(instance.id, 'enablePrefetch', e.target.checked)}
                                            />
                                            <span>Prefetch</span>
                                        </label>
                                    </div>
                                </div>

                                {/* VFS Cache */}
                                <div className={styles.settingsSection}>
                                    <div className={styles.sectionTitle}>VFS Cache</div>
                                    <Form.Group className={styles.formGroupCompact}>
                                        <Form.Control
                                            size="sm"
                                            type="text"
                                            value={instance.vfsCachePath || ''}
                                            onChange={e => updateInstance(instance.id, 'vfsCachePath', e.target.value)}
                                            placeholder="/mnt/nzbdav-cache"
                                        />
                                        <Form.Text muted style={{ fontSize: '0.75rem' }}>
                                            Path to rclone VFS cache for cache detection and migration
                                        </Form.Text>
                                    </Form.Group>
                                </div>

                                {/* Shard Routing */}
                                <div className={styles.settingsSection}>
                                    <div className={styles.sectionTitle}>
                                        Shard Routing
                                        <Form.Check
                                            type="switch"
                                            checked={instance.isShardEnabled}
                                            onChange={e => updateInstance(instance.id, 'isShardEnabled', e.target.checked)}
                                            className={styles.inlineSwitch}
                                        />
                                    </div>

                                    {!instance.isShardEnabled && recommendations[instance.id] && instances.length > 1 && (
                                        <div className={styles.recommendationBanner}>
                                            <span>
                                                Recommended: Shard {recommendations[instance.id].shardIndex + 1}/{recommendations[instance.id].totalShards}
                                                {' '}({recommendations[instance.id].recommendedPrefixes})
                                            </span>
                                            <Button
                                                variant="outline-primary"
                                                size="sm"
                                                onClick={() => applyRecommendation(instance.id)}
                                            >
                                                Apply
                                            </Button>
                                        </div>
                                    )}

                                    {instance.isShardEnabled && (
                                        <div className={styles.shardConfig}>
                                            <div className={styles.compactFormRow}>
                                                <Form.Group className={styles.formGroupCompact} style={{ flex: 0.5 }}>
                                                    <Form.Label>Index</Form.Label>
                                                    <Form.Control
                                                        size="sm"
                                                        type="number"
                                                        min={0}
                                                        max={15}
                                                        value={instance.shardIndex ?? 0}
                                                        onChange={e => updateInstance(instance.id, 'shardIndex', parseInt(e.target.value) || 0)}
                                                    />
                                                </Form.Group>
                                                <Form.Group className={styles.formGroupCompact} style={{ flex: 1 }}>
                                                    <Form.Label>Prefixes</Form.Label>
                                                    <div className="d-flex gap-2">
                                                        <Form.Control
                                                            size="sm"
                                                            type="text"
                                                            value={instance.shardPrefixes || ''}
                                                            onChange={e => updateInstance(instance.id, 'shardPrefixes', e.target.value)}
                                                            placeholder="0-7"
                                                        />
                                                        {recommendations[instance.id] && instance.shardPrefixes !== recommendations[instance.id].recommendedPrefixes && (
                                                            <Button
                                                                variant="outline-secondary"
                                                                size="sm"
                                                                onClick={() => updateInstance(instance.id, 'shardPrefixes', recommendations[instance.id].recommendedPrefixes)}
                                                                title="Use recommended"
                                                            >
                                                                {recommendations[instance.id].recommendedPrefixes}
                                                            </Button>
                                                        )}
                                                    </div>
                                                </Form.Group>
                                            </div>

                                            <div className={styles.shardInfo}>
                                                <code>/instances/{instance.id}</code>
                                                <span className={styles.shardInfoHint}>WebDAV mount path for this shard</span>
                                            </div>

                                            {instance.vfsCachePath && (
                                                <div className={styles.migrationRow}>
                                                    <Button
                                                        variant={
                                                            migrationStates[instance.id] === 'success' ? 'success' :
                                                            migrationStates[instance.id] === 'error' ? 'danger' : 'outline-secondary'
                                                        }
                                                        size="sm"
                                                        onClick={() => migrateCache(instance.id)}
                                                        disabled={migrationStates[instance.id] === 'migrating'}
                                                    >
                                                        {migrationStates[instance.id] === 'migrating' ? (
                                                            <><Spinner animation="border" size="sm" /> Migrating...</>
                                                        ) : (
                                                            "Migrate Cache"
                                                        )}
                                                    </Button>
                                                    <span className={styles.migrationHelp}>
                                                        Move files to correct instance
                                                    </span>
                                                    {migrationResults[instance.id]?.message && (
                                                        <Badge bg="success">{migrationResults[instance.id].message}</Badge>
                                                    )}
                                                    {migrationResults[instance.id]?.error && (
                                                        <Badge bg="danger">{migrationResults[instance.id].error}</Badge>
                                                    )}
                                                </div>
                                            )}
                                        </div>
                                    )}
                                </div>

                                {/* Last Test Info */}
                                {instance.lastTestedAt && (
                                    <div className={styles.lastTestInfo}>
                                        Last tested: {new Date(instance.lastTestedAt).toLocaleString()}
                                        {instance.lastTestError && (
                                            <span className={styles.testError}>{instance.lastTestError}</span>
                                        )}
                                    </div>
                                )}
                            </div>
                        </Collapse>
                    </div>
                );
            })}
        </div>
    );
}
