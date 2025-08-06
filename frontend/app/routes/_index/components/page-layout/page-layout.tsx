import { useCallback, useState } from "react";
import styles from "./page-layout.module.css";

export type PageLayoutProps = {
    topNavComponent: (props: RequiredTopNavProps) => React.ReactNode,
    leftNavChild: React.ReactNode,
    bodyChild: React.ReactNode,
}

export type RequiredTopNavProps = {
    isHamburgerMenuOpen: boolean,
    onHamburgerMenuClick: () => void,
}

export function PageLayout(props: PageLayoutProps) {
    // data
    const [isHamburgerMenuOpen, setIsHamburgerMenuOpen] = useState(false);

    // events
    const onHamburgerMenuClick = useCallback(function () {
        setIsHamburgerMenuOpen(!isHamburgerMenuOpen)
    }, [setIsHamburgerMenuOpen, isHamburgerMenuOpen]);

    const onBodyClick = useCallback(function() {
        setIsHamburgerMenuOpen(false);
    }, [setIsHamburgerMenuOpen]);

    let containerClassName = styles["container"];
    if (isHamburgerMenuOpen) containerClassName += " " + styles["hamburger-open"];

    return (
        <>
            <div className={containerClassName}>
                <div className={styles["top-navigation"]}>
                    <props.topNavComponent
                        isHamburgerMenuOpen={isHamburgerMenuOpen}
                        onHamburgerMenuClick={onHamburgerMenuClick} />
                </div>
                <div className={styles["page"]}>
                    <div className={styles["left-navigation"]}>
                        {props.leftNavChild}
                    </div>
                    <div className={styles["body"]} onClick={onBodyClick}>
                        {props.bodyChild}
                    </div>
                </div>
            </div>
        </>
    );
}