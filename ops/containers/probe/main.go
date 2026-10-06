package main
import("net/http";"os";"time")
func main(){ c:=http.Client{Timeout:3*time.Second}; r,e:=c.Get(os.Args[1]); if e!=nil || r.StatusCode!=200 { os.Exit(1) }; r.Body.Close() }
