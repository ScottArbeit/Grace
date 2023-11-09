param (
    [Parameter(Mandatory=$true)]
    [string]$SecretName,

    [Parameter(Mandatory=$false)]
    [switch]$CreateCommand
)

# Get the secret from Kubernetes
$encodedSecret = kubectl get secret $SecretName -o jsonpath="{.data}"

# Decode the secret
$deserialized = $encodedSecret | ConvertFrom-Json

$decodedSecret = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($deserialized.$SecretName))

# Display the secret
$decodedSecret

# Create a command to set the secret
if ($CreateCommand) {
    "kubectl create secret generic $SecretName --from-literal=$SecretName=""$decodedSecret"""
}