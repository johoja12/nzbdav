import { useState, useEffect, useCallback } from "react";
import { useNavigate } from "react-router";
import styles from "./route.module.css";
import type { Route } from "./+types/route";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "Starting... | NzbDav" },
    { name: "description", content: "NzbDav is starting up" },
  ];
}

export async function loader() {
  // Return build info from environment variables - no backend call needed
  return {
    version: process.env.NZBDAV_VERSION || "unknown",
    buildDate: process.env.NZBDAV_BUILD_DATE || "unknown",
    gitBranch: process.env.NZBDAV_GIT_BRANCH || "unknown",
    gitCommit: process.env.NZBDAV_GIT_COMMIT || "unknown",
    gitRemote: process.env.NZBDAV_GIT_REMOTE || "unknown",
  };
}

type StartupStatus = "checking" | "connecting" | "ready" | "error";

export default function Startup({ loaderData }: Route.ComponentProps) {
  const { version, buildDate, gitBranch, gitCommit, gitRemote } = loaderData;
  const navigate = useNavigate();
  const [status, setStatus] = useState<StartupStatus>("checking");
  const [attempts, setAttempts] = useState(0);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const checkBackend = useCallback(async () => {
    try {
      setStatus("connecting");
      // Use the health endpoint - doesn't require authentication
      const response = await fetch("/health", {
        method: "GET",
        signal: AbortSignal.timeout(5000),
      });

      if (response.ok) {
        setStatus("ready");
        // Small delay to show the "ready" status before redirecting
        setTimeout(() => {
          navigate("/login", { replace: true });
        }, 500);
        return true;
      }

      throw new Error(`Backend returned ${response.status}`);
    } catch (error) {
      setStatus("error");
      setErrorMessage(error instanceof Error ? error.message : "Connection failed");
      setAttempts(prev => prev + 1);
      return false;
    }
  }, [navigate]);

  useEffect(() => {
    let mounted = true;
    let timeoutId: ReturnType<typeof setTimeout>;

    const poll = async () => {
      if (!mounted) return;

      const success = await checkBackend();

      if (!success && mounted) {
        // Retry with exponential backoff (1s, 2s, 3s, max 5s)
        const delay = Math.min(1000 * Math.min(attempts + 1, 5), 5000);
        timeoutId = setTimeout(poll, delay);
      }
    };

    // Initial check after a short delay to ensure page renders
    timeoutId = setTimeout(poll, 500);

    return () => {
      mounted = false;
      clearTimeout(timeoutId);
    };
  }, [checkBackend, attempts]);

  const getStatusMessage = () => {
    switch (status) {
      case "checking":
        return "Initializing...";
      case "connecting":
        return "Connecting to backend...";
      case "ready":
        return "Ready! Redirecting...";
      case "error":
        return `Waiting for backend... (attempt ${attempts})`;
    }
  };

  const getStatusClass = () => {
    switch (status) {
      case "ready":
        return styles.statusReady;
      case "error":
        return styles.statusError;
      default:
        return styles.statusConnecting;
    }
  };

  return (
    <div className={styles.container}>
      <img className={styles.logo} src="/logo.svg" alt="NzbDav Logo" />
      <div className={styles.title}>Nzb DAV</div>

      <div className={styles.statusSection}>
        <div className={`${styles.status} ${getStatusClass()}`}>
          {status !== "ready" && <span className={styles.spinner}></span>}
          {getStatusMessage()}
        </div>
        {errorMessage && status === "error" && (
          <div className={styles.errorDetail}>{errorMessage}</div>
        )}
      </div>

      <div className={styles.buildInfo}>
        <div className={styles.buildRow}>
          <span className={styles.buildLabel}>Version</span>
          <span className={styles.buildValue}>{version}</span>
        </div>
        <div className={styles.buildRow}>
          <span className={styles.buildLabel}>Branch</span>
          <span className={styles.buildValue}>{gitBranch}</span>
        </div>
        <div className={styles.buildRow}>
          <span className={styles.buildLabel}>Commit</span>
          <span className={styles.buildValue}>{gitCommit}</span>
        </div>
        <div className={styles.buildRow}>
          <span className={styles.buildLabel}>Source</span>
          <span className={styles.buildValue}>{gitRemote}</span>
        </div>
        <div className={styles.buildRow}>
          <span className={styles.buildLabel}>Built</span>
          <span className={styles.buildValue}>{buildDate}</span>
        </div>
      </div>
    </div>
  );
}
