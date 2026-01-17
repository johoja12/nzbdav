import { useState, useCallback } from "react";
import { Button, Alert, Spinner } from "react-bootstrap";
import styles from "./populate-strm-library.module.css";

type PopulateStrmLibraryProps = {
    savedConfig: Record<string, string>
};

export function PopulateStrmLibrary({ savedConfig }: PopulateStrmLibraryProps) {
    const [status, setStatus] = useState<{ loading: boolean; message?: string; error?: string }>({ loading: false });

    const isEnabled = savedConfig["api.import-strategy"] === "symlinks" && savedConfig["api.also-create-strm"] === "true";
    const strmLibraryDir = savedConfig["api.strm-library-dir"] || "(not configured)";

    const onPopulateStrmLibrary = useCallback(async () => {
        setStatus({ loading: true });
        try {
            const response = await fetch("/settings/maintenance/populate-strm", { method: "POST" });
            const data = await response.json() as { message?: string; created?: number; skipped?: number; total?: number; error?: string };
            if (!response.ok) {
                throw new Error(data.error || "Failed to populate STRM library");
            }
            setStatus({
                loading: false,
                message: `${data.message} Created: ${data.created}, Skipped: ${data.skipped} (already exist)`
            });
        } catch (error: any) {
            setStatus({
                loading: false,
                error: error.message || "Failed to populate STRM library"
            });
        }
    }, []);

    if (!isEnabled) {
        return (
            <div className={styles.container}>
                <Alert variant="info" className="mb-0">
                    STRM library population is only available when Import Strategy is set to "Symlinks"
                    and "Also create STRM files" is enabled in the Download Client settings.
                </Alert>
            </div>
        );
    }

    return (
        <div className={styles.container}>
            <p>
                Create STRM files for all existing video content in the database. Use this to populate
                your Emby/Jellyfin library with content that was downloaded before enabling dual STRM output.
            </p>
            <p className="text-muted small">
                STRM Library Directory: <code>{strmLibraryDir}</code>
            </p>

            <Button
                variant="primary"
                onClick={onPopulateStrmLibrary}
                disabled={status.loading}
            >
                {status.loading ? (
                    <>
                        <Spinner as="span" animation="border" size="sm" role="status" aria-hidden="true" />
                        {' '}Populating...
                    </>
                ) : (
                    'Populate Existing Content'
                )}
            </Button>

            {status.message && (
                <Alert variant="success" className="mt-3 mb-0">
                    {status.message}
                </Alert>
            )}
            {status.error && (
                <Alert variant="danger" className="mt-3 mb-0">
                    {status.error}
                </Alert>
            )}
        </div>
    );
}
