import { useEffect, useState } from "react";
import { Card, Spinner, Badge, ProgressBar, Table } from "react-bootstrap";
import type { RcloneStatsResponse } from "~/types/rclone";

function formatBytes(bytes: number): string {
    if (bytes === 0) return "0 B";
    const k = 1024;
    const sizes = ["B", "KB", "MB", "GB", "TB"];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
}

function formatSpeed(bytesPerSec: number): string {
    return formatBytes(bytesPerSec) + "/s";
}

export default function RcloneStats() {
    const [stats, setStats] = useState<RcloneStatsResponse[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const fetchStats = async () => {
            try {
                const response = await fetch("/api/stats/rclone");
                if (!response.ok) throw new Error("Failed to fetch rclone stats");
                const data = await response.json();
                setStats(data);
                setError(null);
            } catch (err) {
                setError(err instanceof Error ? err.message : "Unknown error");
            } finally {
                setLoading(false);
            }
        };

        fetchStats();
        const interval = setInterval(fetchStats, 5000);
        return () => clearInterval(interval);
    }, []);

    if (loading) {
        return (
            <div className="text-center py-4">
                <Spinner animation="border" size="sm" /> Loading rclone stats...
            </div>
        );
    }

    if (error) {
        return <div className="text-danger">Error: {error}</div>;
    }

    if (stats.length === 0) {
        return (
            <div className="text-muted">
                No rclone instances configured. Add instances in Settings &gt; Rclone.
            </div>
        );
    }

    return (
        <div>
            {stats.map((instance) => (
                <Card key={instance.instance.id} className="mb-3">
                    <Card.Header className="d-flex justify-content-between align-items-center">
                        <span>
                            <strong>{instance.instance.name}</strong>
                            <span className="text-muted ms-2">
                                {instance.instance.host}:{instance.instance.port}
                            </span>
                        </span>
                        <Badge bg={instance.connected ? "success" : "danger"}>
                            {instance.connected ? `v${instance.version || "?"}` : "Disconnected"}
                        </Badge>
                    </Card.Header>
                    {instance.connected && (
                        <Card.Body>
                            {/* VFS Cache Stats */}
                            {instance.vfsStats && (
                                <div className="mb-3">
                                    <h6>VFS Cache</h6>
                                    <div className="d-flex justify-content-between mb-1">
                                        <span>
                                            {formatBytes(instance.vfsStats.bytesUsed)}
                                            {instance.vfsStats.cacheMaxSize > 0 &&
                                                ` / ${formatBytes(instance.vfsStats.cacheMaxSize)}`}
                                        </span>
                                        <span>{instance.vfsStats.files} files</span>
                                    </div>
                                    {instance.vfsStats.cacheMaxSize > 0 && (
                                        <ProgressBar
                                            now={(instance.vfsStats.bytesUsed / instance.vfsStats.cacheMaxSize) * 100}
                                            variant={instance.vfsStats.outOfSpace ? "danger" : "info"}
                                        />
                                    )}
                                </div>
                            )}

                            {/* Active Transfers */}
                            {instance.vfsTransfers?.transfers && instance.vfsTransfers.transfers.length > 0 && (
                                <div className="mb-3">
                                    <h6>
                                        Active Files ({instance.vfsTransfers.summary?.totalOpenFiles || 0} open)
                                    </h6>
                                    <Table size="sm" striped>
                                        <thead>
                                            <tr>
                                                <th>File</th>
                                                <th>Cache</th>
                                                <th>Status</th>
                                                <th>Speed</th>
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {instance.vfsTransfers.transfers.slice(0, 10).map((t, idx) => (
                                                <tr key={idx}>
                                                    <td className="text-truncate" style={{ maxWidth: "200px" }}>
                                                        {t.name.split("/").pop()}
                                                    </td>
                                                    <td>
                                                        <ProgressBar
                                                            now={t.cachePercentage}
                                                            label={`${t.cachePercentage}%`}
                                                            style={{ minWidth: "60px" }}
                                                            variant={
                                                                t.cacheStatus === "full"
                                                                    ? "success"
                                                                    : t.downloading
                                                                    ? "warning"
                                                                    : "info"
                                                            }
                                                        />
                                                    </td>
                                                    <td>
                                                        <Badge
                                                            bg={
                                                                t.cacheStatus === "full"
                                                                    ? "success"
                                                                    : t.downloading
                                                                    ? "warning"
                                                                    : "secondary"
                                                            }
                                                        >
                                                            {t.downloading ? "Downloading" : t.cacheStatus}
                                                        </Badge>
                                                    </td>
                                                    <td>{t.downloading ? formatSpeed(t.downloadSpeed) : "-"}</td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </Table>
                                    {instance.vfsTransfers.transfers.length > 10 && (
                                        <div className="text-muted small">
                                            ... and {instance.vfsTransfers.transfers.length - 10} more
                                        </div>
                                    )}
                                </div>
                            )}

                            {/* Core Transfer Stats */}
                            {instance.coreStats && (
                                <div className="d-flex gap-4 text-muted small">
                                    <span>Total: {formatBytes(instance.coreStats.bytes)}</span>
                                    <span>Speed: {formatSpeed(instance.coreStats.speed)}</span>
                                    {instance.coreStats.errors > 0 && (
                                        <span className="text-danger">Errors: {instance.coreStats.errors}</span>
                                    )}
                                </div>
                            )}
                        </Card.Body>
                    )}
                    {!instance.connected && (
                        <Card.Body className="text-danger">{instance.error || "Connection failed"}</Card.Body>
                    )}
                </Card>
            ))}
        </div>
    );
}
