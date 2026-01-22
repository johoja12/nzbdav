import { Button, Form, InputGroup, Spinner } from "react-bootstrap";
import { useCallback, useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { TagInput } from "~/components/tag-input/tag-input";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";
import styles from "./sabnzbd.module.css"

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

interface SabServer {
    name: string;
    url: string;
    apiKey: string;
    enabled: boolean;
}

type ConnectionState = 'idle' | 'testing' | 'success' | 'error';

export function SabnzbdSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    // Check if Plex is configured (required for auto-pause)
    const plexServersConfigured = (() => {
        try {
            const servers = JSON.parse(config["plex.servers"] || "[]");
            return Array.isArray(servers) && servers.length > 0;
        } catch {
            return false;
        }
    })();

    // External SABnzbd servers for auto-pause (only enabled if Plex is configured)
    const sabAutoPause = plexServersConfigured && config["sab.auto-pause"] === "true";
    const sabServers: SabServer[] = (() => {
        try {
            const servers = JSON.parse(config["sab.servers"] || "[]");
            if (servers.length > 0) return servers;
        } catch {}
        // Fall back to legacy single-server config
        const legacyUrl = config["sab.url"] || "";
        const legacyApiKey = config["sab.api-key"] || "";
        if (legacyUrl || legacyApiKey) {
            return [{ name: "SABnzbd", url: legacyUrl, apiKey: legacyApiKey, enabled: true }];
        }
        return [];
    })();

    const [sabTestStates, setSabTestStates] = useState<Record<number, ConnectionState>>({});

    const updateSabServers = useCallback((servers: SabServer[]) => {
        setNewConfig(prev => ({
            ...prev,
            "sab.servers": JSON.stringify(servers),
            "sab.url": "",
            "sab.api-key": ""
        }));
    }, [setNewConfig]);

    const addSabServer = useCallback(() => {
        updateSabServers([
            ...sabServers,
            { name: "", url: "", apiKey: "", enabled: true }
        ]);
    }, [sabServers, updateSabServers]);

    const removeSabServer = useCallback((index: number) => {
        updateSabServers(sabServers.filter((_, i) => i !== index));
    }, [sabServers, updateSabServers]);

    const updateSabServer = useCallback((index: number, field: keyof SabServer, value: string | boolean) => {
        updateSabServers(
            sabServers.map((server, i) =>
                i === index ? { ...server, [field]: value } : server
            )
        );
    }, [sabServers, updateSabServers]);

    const testSabConnection = useCallback(async (index: number, url: string, apiKey: string) => {
        if (!url.trim() || !apiKey.trim()) return;

        setSabTestStates(prev => ({ ...prev, [index]: 'testing' }));

        try {
            const formData = new FormData();
            formData.append('url', url);
            formData.append('apiKey', apiKey);

            const response = await fetch('/api/test-sab-connection', {
                method: 'POST',
                body: formData
            });

            const result = await response.json();
            setSabTestStates(prev => ({
                ...prev,
                [index]: result.status && result.connected ? 'success' : 'error'
            }));
        } catch {
            setSabTestStates(prev => ({ ...prev, [index]: 'error' }));
        }
    }, []);

    const onRefreshApiKey = useCallback(() => {
        setNewConfig({ ...config, "api.key": generateNewApiKey() })
    }, [setNewConfig, config]);

    const ensureArticleExistanceSetting =
        useEnsureArticleExistanceSetting(config, setNewConfig);

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="api-key-input">API Key</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        type="text"
                        id="api-key-input"
                        aria-describedby="api-key-help"
                        value={config["api.key"]}
                        readOnly />
                    <Button variant="primary" onClick={onRefreshApiKey}>
                        Refresh
                    </Button>
                </InputGroup>
                <Form.Text id="api-key-help" muted>
                    Use this API key when configuring your download client in Radarr or Sonarr.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="categories-input">Categories</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidCategories(config["api.categories"]) && styles.error])}
                    type="text"
                    id="categories-input"
                    aria-describedby="categories-help"
                    value={config["api.categories"]}
                    placeholder="tv, movies, audio, software"
                    onChange={e => setNewConfig({ ...config, "api.categories": e.target.value })} />
                <Form.Text id="categories-help" muted>
                    Comma-separated categories. Only letters, numbers, and dashes are allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="manual-category-input">Manual Upload Category</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    id="manual-category-input"
                    aria-describedby="manual-category-help"
                    value={config["api.manual-category"]}
                    placeholder="uncategorized"
                    onChange={e => setNewConfig({ ...config, "api.manual-category": e.target.value })} />
                <Form.Text id="manual-category-help" muted>
                    The category to use for manual uploads through the Queue page on the UI.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="import-strategy-input">Import Strategy</Form.Label>
                <Form.Select
                    className={styles.input}
                    value={config["api.import-strategy"]}
                    onChange={e => setNewConfig({ ...config, "api.import-strategy": e.target.value })}
                >
                    <option value="symlinks">Symlinks — Plex</option>
                    <option value="strm">STRM Files — Emby/Jellyfin</option>
                </Form.Select>
                <Form.Text id="import-strategy-help" muted>
                    If you need to be able to stream from Plex, you will need to configure rclone and should select the `Symlinks` option here. If you only need to stream through Emby or Jellyfin, then you can skip rclone altogether and select the `STRM Files` option.
                </Form.Text>
            </Form.Group>
            {/* <hr /> */}
            {config["api.import-strategy"] === 'symlinks' &&
                <Form.Group className={styles.subGroup}>
                    <Form.Label htmlFor="mount-dir-input">Rclone Mount Directory</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="mount-dir-input"
                        aria-describedby="mount-dir-help"
                        placeholder="/mnt/nzbdav"
                        value={config["rclone.mount-dir"]}
                        onChange={e => setNewConfig({ ...config, "rclone.mount-dir": e.target.value })} />
                    <Form.Text id="mount-dir-help" muted>
                        The location at which you've mounted (or will mount) the webdav root, through Rclone. This is used to tell Radarr / Sonarr where to look for completed "downloads."
                    </Form.Text>
                </Form.Group>
            }
            {config["api.import-strategy"] === 'symlinks' &&
                <Form.Group className={styles.subGroup}>
                    <Form.Check
                        type="switch"
                        id="also-create-strm-switch"
                        label="Also create STRM files for Emby/Jellyfin"
                        checked={config["api.also-create-strm"] === "true"}
                        onChange={e => setNewConfig({ ...config, "api.also-create-strm": e.target.checked ? "true" : "false" })} />
                    <Form.Text muted>
                        When enabled, STRM files will also be created alongside symlinks, allowing Emby/Jellyfin to stream content while Plex uses the rclone mount.
                    </Form.Text>
                </Form.Group>
            }
            {config["api.import-strategy"] === 'symlinks' && config["api.also-create-strm"] === "true" &&
                <Form.Group className={styles.subGroup}>
                    <Form.Label htmlFor="strm-library-dir-input">STRM Library Directory</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="strm-library-dir-input"
                        aria-describedby="strm-library-dir-help"
                        placeholder="/data/strm-library"
                        value={config["api.strm-library-dir"]}
                        onChange={e => setNewConfig({ ...config, "api.strm-library-dir": e.target.value })} />
                    <Form.Text id="strm-library-dir-help" muted>
                        Directory where STRM files will be created for Emby/Jellyfin. Point your Emby library to this folder.
                    </Form.Text>
                </Form.Group>
            }
            {config["api.import-strategy"] === 'symlinks' && config["api.also-create-strm"] === "true" &&
                <Form.Group className={styles.subGroup}>
                    <Form.Label htmlFor="strm-base-url-input">STRM Base URL</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="strm-base-url-input"
                        aria-describedby="strm-base-url-help"
                        placeholder="http://192.168.55.175:3000"
                        value={config["general.base-url"]}
                        onChange={e => setNewConfig({ ...config, "general.base-url": e.target.value })} />
                    <Form.Text id="strm-base-url-help" muted>
                        The URL that Emby/Jellyfin will use to stream content. STRM files will contain URLs pointing to this address.
                    </Form.Text>
                </Form.Group>
            }
            {config["api.import-strategy"] === 'strm' && <>
                <Form.Group className={styles.subGroup}>
                    <Form.Label htmlFor="completed-downloads-dir-input">Completed Downloads Dir</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="completed-downloads-dir-input"
                        aria-describedby="completed-downloads-dir-help"
                        placeholder="/data/completed-downloads"
                        value={config["api.completed-downloads-dir"]}
                        onChange={e => setNewConfig({ ...config, "api.completed-downloads-dir": e.target.value })} />
                    <Form.Text id="completed-downloads-dir-help" muted>
                        This is used to tell Radarr / Sonarr where to look for completed "downloads." Make sure this path is also visible to your Radarr / Sonarr containers. The "downloads" placed in this folder will all be *.strm files that point to nzbdav for streaming.
                    </Form.Text>
                </Form.Group>
                <Form.Group className={styles.subGroup}>
                    <Form.Label htmlFor="base-url-input">Base URL</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="base-url-input"
                        aria-describedby="base-url-help"
                        placeholder="http://localhost:3000"
                        value={config["general.base-url"]}
                        onChange={e => setNewConfig({ ...config, "general.base-url": e.target.value })} />
                    <Form.Text id="base-url-help" muted>
                        What is the base URL at which you access nzbdav? Make sure that Emby/Jellyfin can access this url. This is the URL they will connect to for streaming. All *.strm files will point to this URL.
                    </Form.Text>
                </Form.Group>
            </>}
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-queue-connections-input">Max Connections for Queue Processing</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidQueueConnections(config["api.max-queue-connections"]) && styles.error])}
                    type="text"
                    id="max-queue-connections-input"
                    aria-describedby="max-queue-connections-help"
                    placeholder="All"
                    value={config["api.max-queue-connections"]}
                    onChange={e => setNewConfig({ ...config, "api.max-queue-connections": e.target.value })} />
                <Form.Text id="max-queue-connections-help" muted>
                    Queue processing tasks will not use any more than this number of connections. Will default to your overall Max Connections if left empty.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="ignored-files-input">Ignored Files</Form.Label>
                <TagInput
                    className={styles.input}
                    id="ignored-files-input"
                    aria-describedby="ignored-files-help"
                    placeholder="*.nfo, *.par2, *.sfv, *sample.mkv"
                    value={config["api.download-file-blocklist"]}
                    onChange={value => setNewConfig({ ...config, "api.download-file-blocklist": value })} />
                <Form.Text id="ignored-files-help" muted>
                    Files that match these patterns will be ignored and not mounted onto the webdav when processing an nzb. Wildcards (*) are supported.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="duplicate-nzb-input">Behavior for Duplicate NZBs</Form.Label>
                <Form.Select
                    className={styles.input}
                    value={config["api.duplicate-nzb-behavior"]}
                    onChange={e => setNewConfig({ ...config, "api.duplicate-nzb-behavior": e.target.value })}
                >
                    <option value="increment">Download again with suffix (2)</option>
                    <option value="mark-failed">Mark the download as failed</option>
                </Form.Select>
                <Form.Text id="max-queue-connections-help" muted>
                    When an NZB is added, a new folder is created on the webdav. What should be done when the download folder for an NZB already exists?
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="history-retention-hours-input">History Retention (Hours)</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidHistoryRetention(config["api.history-retention-hours"]) && styles.error])}
                    type="text"
                    id="history-retention-hours-input"
                    aria-describedby="history-retention-hours-help"
                    placeholder="24"
                    value={config["api.history-retention-hours"]}
                    onChange={e => setNewConfig({ ...config, "api.history-retention-hours": e.target.value })} />
                <Form.Text id="history-retention-hours-help" muted>
                    Successfully imported or failed history items will be automatically removed after this many hours. Default is 24 hours.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="ensure-importable-video-checkbox"
                    aria-describedby="ensure-importable-video-help"
                    label={`Fail downloads for nzbs without video content`}
                    checked={config["api.ensure-importable-video"] === "true"}
                    onChange={e => setNewConfig({ ...config, "api.ensure-importable-video": "" + e.target.checked })} />
                <Form.Text id="ensure-importable-video-help" muted>
                    Whether to mark downloads as `failed` when no single video file is found inside the nzb. This will force Radarr / Sonarr to automatically look for a new nzb.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="ensure-article-existence-checkbox"
                    aria-describedby="ensure-article-existence-help"
                    label={`Perform article health check during downloads`}
                    ref={ensureArticleExistanceSetting.masterCheckboxRef}
                    checked={!ensureArticleExistanceSetting.areNoneSelected}
                    onChange={e => ensureArticleExistanceSetting.onMasterCheckboxChange(e.target.checked)} />
                <Form.Text id="ensure-article-existence-help" muted>
                    Whether to check for the existence of all articles within an NZB during queue processing. This process may be slow.
                </Form.Text>
                <MultiCheckboxInput
                    options={ensureArticleExistanceSetting.categories}
                    value={config["api.ensure-article-existence-categories"] ?? ""}
                    onChange={value => setNewConfig({ ...config, "api.ensure-article-existence-categories": value })}
                />
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="ignore-history-limit-checkbox"
                    aria-describedby="ignore-history-limit-help"
                    label={`Always send full History to Radarr/Sonarr`}
                    checked={config["api.ignore-history-limit"] === "true"}
                    onChange={e => setNewConfig({ ...config, "api.ignore-history-limit": "" + e.target.checked })} />
                <Form.Text id="ignore-history-limit-help" muted>
                    When enabled, this will ignore the History limit sent by radarr/sonarr and always reply with all History items.&nbsp;
                    <a href="https://github.com/Sonarr/Sonarr/issues/5452">See here</a>.
                </Form.Text>
            </Form.Group>

            {/* Auto-Pause External SABnzbd Section */}
            <hr />
            <h5 className={styles.sectionTitle}>Auto-Pause External SABnzbd</h5>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="sab-auto-pause-checkbox"
                    aria-describedby="sab-auto-pause-help"
                    label="Pause external SABnzbd during media playback"
                    checked={sabAutoPause}
                    disabled={!plexServersConfigured}
                    onChange={e => setNewConfig({ ...config, "sab.auto-pause": "" + e.target.checked })} />
                <Form.Text id="sab-auto-pause-help" muted>
                    {plexServersConfigured ? (
                        "Automatically pause configured SABnzbd servers when verified Plex playback is detected."
                    ) : (
                        "Configure Plex servers in the Integrations tab to enable this feature."
                    )}
                </Form.Text>
            </Form.Group>

            {sabAutoPause && (
                <div className={styles.sabServersSection}>
                    <div className={styles.sabServersHeader}>
                        <span>SABnzbd Servers to Pause</span>
                        <Button variant="outline-secondary" size="sm" onClick={addSabServer}>
                            + Add Server
                        </Button>
                    </div>

                    {sabServers.map((server, index) => (
                        <div key={index} className={styles.sabServerCard}>
                            <button
                                className={styles.closeButton}
                                onClick={() => removeSabServer(index)}
                                title="Remove server"
                                type="button"
                            >
                                &times;
                            </button>

                            <Form.Control
                                className={styles.sabServerInput}
                                type="text"
                                placeholder="Server Name (e.g., NAS01 SABnzbd)"
                                value={server.name}
                                onChange={(e) => updateSabServer(index, 'name', e.target.value)}
                            />

                            <Form.Control
                                className={styles.sabServerInput}
                                type="text"
                                placeholder="URL (e.g., http://192.168.1.100:8080)"
                                value={server.url}
                                onChange={(e) => updateSabServer(index, 'url', e.target.value)}
                            />

                            <InputGroup className={styles.sabServerInput}>
                                <Form.Control
                                    type="password"
                                    placeholder="SABnzbd API Key"
                                    value={server.apiKey}
                                    onChange={(e) => updateSabServer(index, 'apiKey', e.target.value)}
                                />
                                <Button
                                    variant={
                                        sabTestStates[index] === 'success' ? 'success' :
                                        sabTestStates[index] === 'error' ? 'danger' : 'secondary'
                                    }
                                    onClick={() => testSabConnection(index, server.url, server.apiKey)}
                                    disabled={sabTestStates[index] === 'testing'}
                                >
                                    {sabTestStates[index] === 'testing' ? (
                                        <Spinner animation="border" size="sm" />
                                    ) : sabTestStates[index] === 'success' ? (
                                        "OK"
                                    ) : sabTestStates[index] === 'error' ? (
                                        "Fail"
                                    ) : (
                                        "Test"
                                    )}
                                </Button>
                            </InputGroup>

                            <Form.Check
                                type="switch"
                                id={`sab-server-enabled-${index}`}
                                label="Enabled"
                                checked={server.enabled}
                                onChange={(e) => updateSabServer(index, 'enabled', e.target.checked)}
                            />
                        </div>
                    ))}

                    {sabServers.length === 0 && (
                        <Form.Text muted className="d-block mt-2">
                            No SABnzbd servers configured. Add a server to enable auto-pause.
                        </Form.Text>
                    )}
                </div>
            )}
        </div>
    );
}

function useEnsureArticleExistanceSetting(
    config: Record<string, string>,
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
) {
    const manualCategoryValue = config["api.manual-category"];
    const categoriesValue = config["api.categories"];
    const healthCheckCategoriesValue = config["api.ensure-article-existence-categories"];

    const manualCategory = useMemo(() => {
        return !!(manualCategoryValue?.trim())
            ? manualCategoryValue.trim()
            : "uncategorized";
    }, [manualCategoryValue]);

    const categories = useMemo(() => {
        var list = !!(categoriesValue?.trim())
            ? categoriesValue.split(",").map(c => c.trim()).filter(c => c.length > 0)
            : ["audio", "software", "tv", "movies"];
        return [manualCategory, ...list];
    }, [categoriesValue]);

    const healthCheckCategories = useMemo(() => {
        const cats = healthCheckCategoriesValue;
        if (!cats || cats.trim() === "") return [];
        return cats.split(",").map(c => c.trim()).filter(c => c.length > 0);
    }, [healthCheckCategoriesValue]);

    const masterCheckboxRef = useRef<HTMLInputElement>(null);
    const areAllSelected = categories.length > 0 && categories.every(c => healthCheckCategories.includes(c));
    const areNoneSelected = healthCheckCategories.length === 0 || categories.every(c => !healthCheckCategories.includes(c));
    const areSomeSelected = !areAllSelected && !areNoneSelected;

    useEffect(() => {
        if (masterCheckboxRef.current) {
            masterCheckboxRef.current.indeterminate = areSomeSelected;
        }
    }, [areSomeSelected]);

    const onMasterCheckboxChange = useCallback((checked: boolean) => {
        if (checked) {
            setNewConfig(prev => ({ ...prev, "api.ensure-article-existence-categories": categories.join(", ") }));
        } else {
            setNewConfig(prev => ({ ...prev, "api.ensure-article-existence-categories": "" }));
        }
    }, [setNewConfig, categories]);

    return {
        categories,
        masterCheckboxRef,
        areAllSelected,
        areNoneSelected,
        areSomeSelected,
        onMasterCheckboxChange
    }
}

export function isSabnzbdSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["api.key"] !== newConfig["api.key"]
        || config["api.categories"] !== newConfig["api.categories"]
        || config["api.manual-category"] !== newConfig["api.manual-category"]
        || config["rclone.mount-dir"] !== newConfig["rclone.mount-dir"]
        || config["api.max-queue-connections"] !== newConfig["api.max-queue-connections"]
        || config["api.ensure-importable-video"] !== newConfig["api.ensure-importable-video"]
        || config["api.ensure-article-existence-categories"] !== newConfig["api.ensure-article-existence-categories"]
        || config["api.ignore-history-limit"] !== newConfig["api.ignore-history-limit"]
        || config["api.duplicate-nzb-behavior"] !== newConfig["api.duplicate-nzb-behavior"]
        || config["api.download-file-blocklist"] !== newConfig["api.download-file-blocklist"]
        || config["api.import-strategy"] !== newConfig["api.import-strategy"]
        || config["api.completed-downloads-dir"] !== newConfig["api.completed-downloads-dir"]
        || config["general.base-url"] !== newConfig["general.base-url"]
        || config["api.history-retention-hours"] !== newConfig["api.history-retention-hours"]
        || config["sab.auto-pause"] !== newConfig["sab.auto-pause"]
        || config["sab.servers"] !== newConfig["sab.servers"]
        || config["sab.url"] !== newConfig["sab.url"]
        || config["sab.api-key"] !== newConfig["sab.api-key"]
        || config["api.also-create-strm"] !== newConfig["api.also-create-strm"]
        || config["api.strm-library-dir"] !== newConfig["api.strm-library-dir"]
}

export function isSabnzbdSettingsValid(newConfig: Record<string, string>) {
    return isValidCategories(newConfig["api.categories"])
        && isValidQueueConnections(newConfig["api.max-queue-connections"])
        && isValidHistoryRetention(newConfig["api.history-retention-hours"]);
}

export function generateNewApiKey(): string {
    return crypto.randomUUID().toString().replaceAll("-", "");
}

function isValidCategories(categories: string): boolean {
    if (categories === "") return true;
    var parts = categories.split(",");
    return parts.map(x => x.trim()).every(x => isAlphaNumericWithDashes(x));
}

function isAlphaNumericWithDashes(input: string): boolean {
    const regex = /^[A-Za-z0-9-]+$/;
    return regex.test(input);
}

function isValidQueueConnections(maxQueueConnections: string): boolean {
    return maxQueueConnections === "" || isPositiveInteger(maxQueueConnections);
}

function isValidHistoryRetention(historyRetentionHours: string): boolean {
    return historyRetentionHours === "" || isPositiveInteger(historyRetentionHours);
}
