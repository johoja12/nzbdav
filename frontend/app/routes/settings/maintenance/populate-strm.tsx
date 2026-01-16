import { type ActionFunctionArgs } from "react-router";

export async function action({ request }: ActionFunctionArgs) {
    if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });

    try {
        const url = process.env.BACKEND_URL + "/api/maintenance/populate-strm";
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";

        const response = await fetch(url, {
            method: "POST",
            headers: {
                "x-api-key": apiKey,
                "Content-Type": "application/json"
            }
        });

        const data = await response.json();

        if (!response.ok) {
            return new Response(JSON.stringify({ error: data.error || "Failed to populate STRM library" }), {
                status: response.status,
                headers: { "Content-Type": "application/json" }
            });
        }

        return new Response(JSON.stringify(data), {
            status: 200,
            headers: { "Content-Type": "application/json" }
        });
    } catch (error: any) {
        return new Response(JSON.stringify({ error: error.message }), {
            status: 500,
            headers: { "Content-Type": "application/json" }
        });
    }
}
