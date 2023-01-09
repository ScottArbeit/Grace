$startTime = Get-Date

1..1 | ForEach-Object -Parallel {
    $words = "Sit fusce at sociosqu eros bibendum aliquet cursus ante non facilisis tempor Scelerisque arcu potenti feugiat fermentum viverra et litora facilisis vestibulum sit aliquam quisque sagittis ut Ultricies nisi urna cursus tellus tempor vivamus nec Dictumst tristique porta vel cubilia mollis Tempus nullam laoreet sit vestibulum etiam in volutpat dui class netus morbi Duis facilisis at aliquet fusce nisi Nulla arcu molestie mauris integer aenean ligula curabitur dui sociosqu suspendisse mi fringilla faucibus Rhoncus habitasse massa amet ipsum ligula quisque Quisque fames bibendum eu ullamcorper pulvinar in aenean hendrerit Augue tristique aenean amet auctor curabitur congue placerat aenean posuere porttitor pulvinar lectus Mattis aenean elit condimentum nam iaculis ante felis sollicitudin Risus viverra ornare curabitur sem massa nibh vulputate senectus dictum vitae leo varius dictumst tristique Ultrices ut blandit adipiscing dictumst sagittis elementum urna Vel feugiat consectetur malesuada nibh turpis odio convallis molestie vulputate magna venenatis lacinia Suscipit consequat lectus nullam suspendisse aliquam sed venenatis Feugiat vehicula iaculis donec aenean Volutpat amet feugiat fringilla bibendum scelerisque fermentum pellentesque hendrerit dapibus primis eu ipsum proin mauris Amet magna non mattis dictum risus sit Luctus hendrerit in integer euismod sapien aenean vel maecenas venenatis lorem cubilia taciti Id mauris dictum aenean leo quisque auctor sagittis nisl rutrum at Iaculis luctus orci egestas metus commodo praesent sodales nam quis conubia cras sagittis vestibulum Viverra justo cursus tempor fringilla egestas Potenti aliquam quisque tincidunt pellentesque Lacinia eu convallis quis risus accumsan Augue adipiscing orci massa lorem curabitur eleifend tincidunt justo varius vulputate Mollis aenean est pulvinar proin in donec bibendum dolor quis sociosqu mattis mi Euismod urna leo mollis potenti fames mattis ultrices diam Vivamus sit mattis vehicula viverra mi imperdiet Adipiscing est vehicula scelerisque velit Malesuada integer quisque fusce quis mollis eros Leo nec tellus curabitur ornare amet quisque fusce habitasse morbi Sem lacinia eu aenean pretium curae dolor cubilia faucibus purus Sollicitudin nisl tempus auctor etiam felis urna consectetur donec dui Posuere elit orci lobortis magna Enim at pellentesque ac taciti convallis sapien ad elit Integer potenti malesuada lacinia fames euismod amet purus justo sociosqu dolor cras tempus dictumst Dictumst adipiscing quisque sapien pharetra pretium aliquam nunc ipsum varius mi justo aenean mattis Aenean conubia felis inceptos nulla ante sociosqu libero non imperdiet Nunc feugiat sodales commodo interdum rhoncus nulla aliquet cras sociosqu eros sed Vivamus varius sapien sollicitudin curabitur class aenean tempus tempor magna donec bibendum nulla morbi semper Praesent inceptos etiam tempus in Varius hac et feugiat nullam dictum vivamus adipiscing ut in eros nulla molestie ante Interdum dictum volutpat accumsan posuere quis amet curae nostra purus fusce nisl lacus Aenean erat suscipit urna ante In ad varius interdum porta at pulvinar aptent enim nam sit ultrices hendrerit Vitae rhoncus consequat non metus nullam augue Massa vestibulum dapibus lectus nibh at tortor ullamcorper mattis rutrum pellentesque aliquam adipiscing porttitor".Split()
    $suffix = (Get-Random -Maximum 65536).ToString("X4")

    $ownerId = (New-Guid).ToString()
    $ownerName = 'Owner' + $suffix
    $organizationId = (New-Guid).ToString()
    $orgName = 'Org' + $suffix
    $repoId = (New-Guid).ToString()
    $repoName = 'Repo' + $suffix
    $branchId = (New-Guid).ToString()
    $branchName = 'Branch' + $suffix

    C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe owner create --output Json --ownerName $ownerName
    C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe org create --output Json --ownerName $ownerName --organizationName $orgName
    C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe repo create --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName
    C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe branch create --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName --branchName $branchName

    1..50 | ForEach-Object {
        $numberOfWords = Get-Random -Minimum 3 -Maximum 9
        $start = Get-Random -Minimum 0 -Maximum ($words.Count - $numberOfWords)
        $message = ''
        for ($i = $0; $i -lt $numberOfWords; $i++) {
            $message += $words[$i + $start] + " "
        }

        switch (Get-Random -Maximum 4) {
            0 {C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe branch save --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName --branchName $branchName}
            1 {C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe branch checkpoint --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName --branchName $branchName -m $message}
            2 {C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe branch commit --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName --branchName $branchName -m $message}
            3 {C:\Source\Grace\src\Grace.CLI\bin\Debug\net7.0\win10-x64\Grace.CLI.exe branch tag --output Json --ownerName $ownerName --organizationName $orgName --repositoryName $repoName --branchName $branchName -m $message}
        }
    }
} -ThrottleLimit 8

$endTime = Get-Date
$elapsed = $endTime.Subtract($startTime).TotalSeconds.ToString("F3")
"Elapsed: $elapsed seconds"
