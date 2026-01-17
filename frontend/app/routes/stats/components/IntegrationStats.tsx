import { useEffect, useState } from "react";
import { Card, Table, Badge, Alert, Spinner } from "react-bootstrap";

interface ServerHealthDto {
    serverName: string;
    serverType: string;
    isReachable: boolean;
    lastChecked: string;
    lastReachable?: string;
    lastError?: string;
    consecutiveFailures: number;
    version?: string;
}

interface IntegrationHealthResponse {
    status: boolean;
    plex: Record<string, ServerHealthDto>;
    emby: Record<string, ServerHealthDto>;
    sabnzbd: Record<string, ServerHealthDto>;
    radarr: Record<string, ServerHealthDto>;
    sonarr: Record<string, ServerHealthDto>;
    rclone: Record<string, ServerHealthDto>;
}

function formatTimeAgo(isoString: string | undefined): string {
    if (!isoString || isoString === "0001-01-01T00:00:00") return "-";
    const date = new Date(isoString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffSec = Math.floor(diffMs / 1000);

    if (diffSec < 0) return "just now";
    if (diffSec < 60) return `${diffSec}s ago`;
    if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m ago`;
    if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h ago`;
    return `${Math.floor(diffSec / 86400)}d ago`;
}

interface IntegrationTableProps {
    title: string;
    servers: ServerHealthDto[];
    badgeColor: string;
}

function IntegrationTable({ title, servers, badgeColor }: IntegrationTableProps) {
    if (servers.length === 0) return null;

    return (
        <div className="col-12 col-xl-6">
            <Card className="bg-dark border-secondary h-100">
                <Card.Header className="d-flex align-items-center gap-2">
                    <span className="fw-bold">{title}</span>
                    <Badge bg={badgeColor}>{servers.length}</Badge>
                </Card.Header>
                <Card.Body className="p-0">
                    <Table variant="dark" className="mb-0" size="sm">
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>Status</th>
                                <th>Last Checked</th>
                                <th>Error</th>
                            </tr>
                        </thead>
                        <tbody>
                            {servers.map((server) => (
                                <tr key={server.serverName}>
                                    <td className="fw-medium">{server.serverName}</td>
                                    <td>
                                        <Badge bg={server.isReachable ? "success" : "danger"}>
                                            {server.isReachable ? "Online" : "Offline"}
                                        </Badge>
                                    </td>
                                    <td>{formatTimeAgo(server.lastChecked)}</td>
                                    <td className="text-truncate" style={{ maxWidth: "200px" }}>
                                        {server.lastError ? (
                                            <span className="text-danger small" title={server.lastError}>
                                                {server.lastError.length > 40
                                                    ? server.lastError.substring(0, 40) + "..."
                                                    : server.lastError}
                                            </span>
                                        ) : (
                                            <span className="text-muted">-</span>
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </Table>
                </Card.Body>
            </Card>
        </div>
    );
}

export function IntegrationStats() {
    const [health, setHealth] = useState<IntegrationHealthResponse | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);

    useEffect(() => {
        const fetchHealth = async () => {
            try {
                const response = await fetch("/api/server-health");
                if (!response.ok) {
                    throw new Error(`HTTP ${response.status}: ${response.statusText}`);
                }
                const data = await response.json();
                setHealth(data);
                setError(null);
            } catch (e) {
                setError(e instanceof Error ? e.message : "Failed to fetch integration health");
            } finally {
                setLoading(false);
            }
        };

        fetchHealth();
        const interval = setInterval(fetchHealth, 5000);
        return () => clearInterval(interval);
    }, []);

    if (loading) {
        return (
            <div className="text-center py-4">
                <Spinner animation="border" size="sm" className="me-2" />
                Loading integration status...
            </div>
        );
    }

    if (error) {
        return <Alert variant="danger">Error: {error}</Alert>;
    }

    const plexServers = health?.plex ? Object.values(health.plex) : [];
    const embyServers = health?.emby ? Object.values(health.emby) : [];
    const sabServers = health?.sabnzbd ? Object.values(health.sabnzbd) : [];
    const radarrServers = health?.radarr ? Object.values(health.radarr) : [];
    const sonarrServers = health?.sonarr ? Object.values(health.sonarr) : [];
    const rcloneServers = health?.rclone ? Object.values(health.rclone) : [];

    const totalIntegrations = plexServers.length + embyServers.length + sabServers.length +
        radarrServers.length + sonarrServers.length + rcloneServers.length;

    if (totalIntegrations === 0) {
        return (
            <Alert variant="info">
                No integrations configured. Configure integrations in Settings.
            </Alert>
        );
    }

    // Count online/offline
    const allServers = [...plexServers, ...embyServers, ...sabServers, ...radarrServers, ...sonarrServers, ...rcloneServers];
    const onlineCount = allServers.filter(s => s.isReachable).length;
    const offlineCount = allServers.filter(s => !s.isReachable).length;

    return (
        <div>
            {/* Summary badges */}
            <div className="mb-3 d-flex gap-2">
                <Badge bg="success" className="fs-6">
                    {onlineCount} Online
                </Badge>
                {offlineCount > 0 && (
                    <Badge bg="danger" className="fs-6">
                        {offlineCount} Offline
                    </Badge>
                )}
            </div>

            <div className="row g-3">
                <IntegrationTable title="Plex" servers={plexServers} badgeColor="warning" />
                <IntegrationTable title="Emby" servers={embyServers} badgeColor="success" />
                <IntegrationTable title="SABnzbd" servers={sabServers} badgeColor="info" />
                <IntegrationTable title="Radarr" servers={radarrServers} badgeColor="primary" />
                <IntegrationTable title="Sonarr" servers={sonarrServers} badgeColor="primary" />
                <IntegrationTable title="Rclone" servers={rcloneServers} badgeColor="secondary" />
            </div>
        </div>
    );
}
