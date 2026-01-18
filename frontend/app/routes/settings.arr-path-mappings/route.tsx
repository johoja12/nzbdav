import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { redirect } from "react-router";
import { isAuthenticated } from "~/auth/authentication.server";

// Resource route for arr-path-mappings API calls
// Handles: GET mappings, POST save, POST test, POST root-folders

export async function loader({ request }: Route.LoaderArgs) {
    // GET - fetch all path mappings
    if (!await isAuthenticated(request)) return redirect("/login");

    const url = new URL(request.url);
    const action = url.searchParams.get("action");

    if (action === "get") {
        const result = await backendClient.getArrPathMappings();
        return result;
    }

    return { status: false, error: "Unknown action" };
}

export async function action({ request }: Route.ActionArgs) {
    // POST actions
    if (!await isAuthenticated(request)) return redirect("/login");

    const formData = await request.formData();
    const action = formData.get("action")?.toString();

    if (action === "save") {
        // Save path mappings
        const host = formData.get("host")?.toString() || "";
        const mappingsJson = formData.get("mappings")?.toString() || "[]";

        const result = await backendClient.saveArrPathMappings(host, mappingsJson);
        return result;
    }

    if (action === "test") {
        // Test path mapping
        const host = formData.get("host")?.toString() || "";
        const apiKey = formData.get("apiKey")?.toString() || "";
        const nzbdavPrefix = formData.get("nzbdavPrefix")?.toString() || "";
        const arrPrefix = formData.get("arrPrefix")?.toString() || "";

        const result = await backendClient.testArrPathMapping(host, apiKey, nzbdavPrefix, arrPrefix);
        return result;
    }

    if (action === "root-folders") {
        // Get root folders from Arr
        const host = formData.get("host")?.toString() || "";
        const apiKey = formData.get("apiKey")?.toString() || "";

        const result = await backendClient.getArrRootFolders(host, apiKey);
        return result;
    }

    return { status: false, error: "Unknown action" };
}
