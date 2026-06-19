on run argv
	if (count of argv) is not 6 then
		error "Usage: word-compare.scpt <originalPath> <revisedPath> <outputPath> <author> <mode> <workspaceFolder>"
	end if
	set originalPath to item 1 of argv
    set revisedPath to item 2 of argv
    set outputPath to item 3 of argv
    set authorName to item 4 of argv
    set compareMode to item 5 of argv
    set workspaceFolder to item 6 of argv
    set strictMode to compareMode is "strict"
    set detectFormatChangesFlag to not strictMode

    -- Resolve the PARENT of the workspace folder and pick a localized
    -- prompt OUTSIDE the Word "tell" block, so Standard Additions run
    -- in the script's own context (reliable, no Word dependency).
    set parentPath to do shell script "/usr/bin/dirname " & quoted form of workspaceFolder
    set localeCode to do shell script "/usr/bin/defaults read -g AppleLocale 2>/dev/null || echo en"
    if localeCode starts with "de" then
        set folderPrompt to "Wähle den Ordner „compare“ aus, damit Microsoft Word auf die Vergleichsdateien zugreifen darf:"
    else
        set folderPrompt to "Select the “compare” folder to grant Microsoft Word access to the comparison files:"
    end if

    tell application "Microsoft Word"
        activate
        set previousUserName to user name
        try
            set user name to authorName
            try
                open (POSIX file originalPath)
           	on error errMsg number errNum
           		-- User-cancelled chooser: propagate immediately.
           		if errNum is -128 then
           			error errMsg number errNum
           		end if
           		-- Permission failure: prompt the user to grant folder
           		-- access. Default location is the PARENT of the workspace
           		-- so the "compare" folder itself is directly selectable
           		-- (instead of opening inside it with no way to pick it).
           		choose folder with prompt folderPrompt default location (POSIX file parentPath) without multiple selections allowed
           		-- Retry the open now that Word has been granted access.
           		open (POSIX file originalPath)
           	end try
            set originalDoc to active document
            compare originalDoc path revisedPath author name authorName target compare target new detect format changes detectFormatChangesFlag ignore all comparison warnings true add to recent files false
            set compareDoc to active document
            save as compareDoc file name (POSIX file outputPath)
            close compareDoc saving no
            close originalDoc saving no
        on error errMsg number errNum
            try
                if (exists active document) then
                    close active document saving no
                end if
            end try
            set user name to previousUserName
            error errMsg number errNum
        end try
        set user name to previousUserName
    end tell
end run
