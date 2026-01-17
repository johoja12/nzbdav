import {
  Links,
  Meta,
  Outlet,
  redirect,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
} from "react-router";

import 'bootstrap/dist/css/bootstrap.min.css';
import "./app.css";
import type { Route } from "./+types/root";
import { IS_FRONTEND_AUTH_DISABLED, isAuthenticated } from "~/auth/authentication.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";
import { backendClient } from "~/clients/backend-client.server";
import { ToastProvider } from "./context/ToastContext";
import { ConfirmProvider } from "./context/ConfirmContext";

export function meta({}: Route.MetaArgs) {
  return [
    { title: "NzbDav" },
    { name: "description", content: "NzbDav Web Interface" },
  ];
}

export async function loader({ request }: Route.LoaderArgs) {
  // unauthenticated routes that don't need backend
  let path = new URL(request.url).pathname;
  if (path === "/startup") return { useLayout: false };
  if (path === "/login") return { useLayout: false };
  if (path === "/onboarding") return { useLayout: false };

  // Try to connect to backend - redirect to startup page if unavailable
  try {
    // ensure all other routes are authenticated
    if (!await isAuthenticated(request)) return redirect("/login");

    var config = await backendClient.getConfig(["stats.enable"]);
    var statsEnabled = config.find(x => x.configName === "stats.enable")?.configValue !== "false";

    return {
      useLayout: true,
      version: process.env.NZBDAV_VERSION,
      buildDate: process.env.NZBDAV_BUILD_DATE,
      gitBranch: process.env.NZBDAV_GIT_BRANCH,
      gitCommit: process.env.NZBDAV_GIT_COMMIT,
      gitRemote: process.env.NZBDAV_GIT_REMOTE,
      gitUpstream: process.env.NZBDAV_GIT_UPSTREAM,
      upstreamDate: process.env.NZBDAV_UPSTREAM_DATE,
      originalDate: process.env.NZBDAV_ORIGINAL_DATE,
      isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
      statsEnabled: statsEnabled
    };
  } catch (error) {
    // Backend is not available - redirect to startup page
    console.error("Backend unavailable, redirecting to startup page:", error);
    return redirect("/startup");
  }
}


export function Layout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-bs-theme="dark">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <link rel="icon" href="/logo.svg" />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const { useLayout, version, buildDate, gitBranch, gitCommit, gitRemote, gitUpstream, upstreamDate, originalDate, isFrontendAuthDisabled, statsEnabled } = loaderData;
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);

  if (useLayout) {
    return (
      <ToastProvider>
        <ConfirmProvider>
          <PageLayout
            topNavComponent={TopNavigation}
            bodyChild={showLoading ? <Loading /> : <Outlet />}
            leftNavChild={
              <LeftNavigation
                version={version}
                buildDate={buildDate}
                gitBranch={gitBranch}
                gitCommit={gitCommit}
                gitRemote={gitRemote}
                gitUpstream={gitUpstream}
                upstreamDate={upstreamDate}
                originalDate={originalDate}
                isFrontendAuthDisabled={isFrontendAuthDisabled}
                statsEnabled={statsEnabled} />
            } />
        </ConfirmProvider>
      </ToastProvider>
    );
  }

  return (
    <ToastProvider>
      <ConfirmProvider>
        <Outlet />
      </ConfirmProvider>
    </ToastProvider>
  );
}