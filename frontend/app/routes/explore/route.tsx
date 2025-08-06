import type { Route } from "./+types/route";
import { Breadcrumbs } from "./breadcrumbs/breadcrumbs";
import styles from "./route.module.css"
import { Link, redirect, useLocation, useNavigate } from "react-router";
import { backendClient, type DirectoryItem } from "~/clients/backend-client.server";
import { sessionStorage } from "~/auth/authentication.server";
import { useCallback } from "react";
import { lookup as getMimeType } from 'mime-types';
import { getDownloadKey } from "~/auth/downloads.server";

export type ExplorePageData = {
    parentDirectories: string[],
    items: (DirectoryItem | ExploreFile)[],
}

export type ExploreFile = DirectoryItem & {
    mimeType: string,
    downloadKey: string,
}


export async function loader({ request }: Route.LoaderArgs) {
    let session = await sessionStorage.getSession(request.headers.get("cookie"));
    let user = session.get("user");
    if (!user) return redirect("/login");

    let path = new URL(request.url).pathname;
    if (path.startsWith("/")) path = path.slice(1);
    if (path.startsWith("explore")) path = path.slice(7);
    if (path.startsWith("/")) path = path.slice(1);

    return {
        parentDirectories: path == "" ? [] : path.split('/'),
        items: (await backendClient.listWebdavDirectory(path)).map(x => {
            if (x.isDirectory) return x;
            return {
                ...x,
                mimeType: getMimeType(x.name),
                downloadKey: getDownloadKey(`${path}/${x.name}`)
            };
        })
    }
}


export default function Explore({ loaderData }: Route.ComponentProps) {
    return (
        <Body {...loaderData} />
    );
}

function Body({ parentDirectories, items }: ExplorePageData) {
    const location = useLocation();
    const navigate = useNavigate();

    const getDirectoryPath = useCallback((directoryName: string) => {
        return `${location.pathname}/${directoryName}`
    }, [location.pathname, navigate]);

    const getFilePath = useCallback((file: ExploreFile) => {
        var pathname = location.pathname;
        if (pathname.startsWith("/")) pathname = pathname.slice(1);
        if (pathname.startsWith("explore")) pathname = pathname.slice(7);
        if (pathname.startsWith("/")) pathname = pathname.slice(1);
        return `/view/${pathname}/${file.name}?downloadKey=${file.downloadKey}`;
    }, [location.pathname, navigate]);

    return (
        <div className={styles.container}>
            <Breadcrumbs parentDirectories={parentDirectories} />
            <div>
                {items.filter(x => x.isDirectory).map((x, index) =>
                    <Link key={`${index}_dir_item`} to={getDirectoryPath(x.name)} className={styles.item}>
                        <div className={styles["directory-icon"]} />
                        <div className={styles["item-name"]}>{x.name}</div>
                    </Link>
                )}
                {items.filter(x => !x.isDirectory).map((x, index) =>
                    <a key={`${index}_file_item`} href={getFilePath(x as ExploreFile)} className={styles.item}>
                        <div className={getIcon(x as ExploreFile)} />
                        <div className={styles["item-name"]}>{x.name}</div>
                    </a>
                )}
            </div>
        </div >
    );
}


function getIcon(file: ExploreFile) {
    if (file.name.toLowerCase().endsWith(".mkv")) return styles["video-icon"];
    if (file.mimeType && file.mimeType.startsWith("video")) return styles["video-icon"];
    if (file.mimeType && file.mimeType.startsWith("image")) return styles["image-icon"];
    return styles["file-icon"];
}
