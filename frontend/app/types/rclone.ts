export interface RcloneInstance {
    id: string;
    name: string;
    host: string;
    port: number;
    username?: string;
    password?: string;
    remoteName: string;
    isEnabled: boolean;
    enableDirRefresh: boolean;
    enablePrefetch: boolean;
    vfsCachePath?: string;
    createdAt: string;
    lastTestedAt?: string;
    lastTestSuccess?: boolean;
    lastTestError?: string;
}

export interface RcloneTestResult {
    success: boolean;
    version?: string;
    message: string;
}

export interface RcloneCoreStats {
    bytes: number;
    transfers: number;
    speed: number;
    errors: number;
    lastError?: string;
    transferring?: RcloneTransferItem[];
}

export interface RcloneTransferItem {
    name: string;
    bytes: number;
    size: number;
    percentage: number;
    speed: number;
    speedAvg: number;
    eta?: number;
}

export interface RcloneVfsStats {
    bytesUsed: number;
    files: number;
    outOfSpace: boolean;
    uploadsInProgress: number;
    uploadsQueued: number;
    cacheMaxSize: number;
}

export interface RcloneVfsTransfersSummary {
    activeDownloads: number;
    activeReads: number;
    totalOpenFiles: number;
    outOfSpace: boolean;
    totalCacheBytes: number;
    totalCacheFiles: number;
}

export interface RcloneVfsTransferItem {
    name: string;
    size: number;
    opens: number;
    dirty: boolean;
    lastAccess?: string;
    cacheBytes: number;
    cachePercentage: number;
    cacheStatus: string;
    downloading: boolean;
    downloadBytes: number;
    downloadSpeed: number;
    downloadSpeedAvg: number;
    readBytes: number;
    readOffset: number;
    readOffsetPercentage: number;
    readSpeed: number;
}

export interface RcloneStatsResponse {
    instance: {
        id: string;
        name: string;
        host: string;
        port: number;
    };
    connected: boolean;
    version?: string;
    error?: string;
    coreStats?: RcloneCoreStats;
    vfsStats?: RcloneVfsStats;
    vfsTransfers?: {
        summary?: RcloneVfsTransfersSummary;
        transfers?: RcloneVfsTransferItem[];
    };
}
