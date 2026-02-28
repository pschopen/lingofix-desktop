on run argv
	if (count of argv) is not 5 then
		error "Usage: word-compare.scpt <originalPath> <revisedPath> <outputPath> <author> <mode>"
	end if
	set originalPath to item 1 of argv
    set revisedPath to item 2 of argv
    set outputPath to item 3 of argv
    set authorName to item 4 of argv
    set compareMode to item 5 of argv
    set strictMode to compareMode is "strict"
    set detectFormatChangesFlag to not strictMode

    tell application "Microsoft Word"
        activate
        set previousUserName to user name
        try
            set user name to authorName
            open (POSIX file originalPath)
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
